using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace DecoPlotter.Deco;

/// <summary>
/// Client for the local (LuCI based) TP-Link Deco API. Protocol: RSA-encrypted password, AES-CBC encrypted
/// payloads, RSA-signed requests. Modelled after the open source amosyuen/ha-tplink-deco integration.
/// </summary>
public sealed partial class DecoClient : IDisposable
{
    private readonly DecoOptions _opt;
    private readonly ILogger<DecoClient> _log;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string _baseUrl;

    private string? _aesKey, _aesIv;
    private RSAParameters? _passwordKey, _signKey;
    private long _seq;
    private string? _stok, _cookie;

    public DecoClient(IOptions<DecoOptions> options, ILogger<DecoClient> log)
    {
        _opt = options.Value;
        _log = log;
        var handler = new HttpClientHandler
        {
            // Decos use a self-signed certificate and may redirect http->https; we handle redirects ourselves to keep the cookie.
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            AllowAutoRedirect = false,
            UseCookies = false,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(_opt.RequestTimeoutSeconds) };
        _baseUrl = _opt.Host.Contains("://") ? _opt.Host.TrimEnd('/') : $"http://{_opt.Host}";
    }

    public async Task<IReadOnlyList<DecoClientInfo>> GetClientsAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (_stok is null) await LoginAsync(ct);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var result = await CallAsync("admin/client", "client_list",
                        new { operation = "read", @params = new { device_mac = "default" } }, ct);
                    _log.LogDebug("client_list took {Ms} ms", sw.ElapsedMilliseconds);
                    return ParseClients(result);
                }
                catch (Exception ex) when (attempt == 0 && ex is not DecoAuthException && (ex is DecoApiException or HttpRequestException || IsTimeout(ex, ct)))
                {
                    _log.LogInformation("Deco call failed ({Message}); retrying", ex.Message);
                    if (ex is DecoApiException or HttpRequestException) ClearAuth();
                }
            }
        }
        finally { _lock.Release(); }
    }

    private static bool IsTimeout(Exception ex, CancellationToken ct) => ex is TaskCanceledException && !ct.IsCancellationRequested;

    private IReadOnlyList<DecoClientInfo> ParseClients(JsonElement result)
    {
        var list = new List<DecoClientInfo>();
        foreach (var c in result.GetProperty("client_list").EnumerateArray())
        {
            list.Add(new DecoClientInfo(
                Mac: Str(c, "mac").ToUpperInvariant().Replace(':', '-'),
                Name: DecodeName(Str(c, "name")),
                Ip: Str(c, "ip"),
                Online: !c.TryGetProperty("online", out var on) || on.ToString().ToLowerInvariant() is not ("0" or "false"),
                ConnectionType: Str(c, "connection_type"),
                Interface: Str(c, "interface"),
                AccessPoint: c.TryGetProperty("access_host", out var ah) ? ah.ToString() : null,
                UpBytesPerSec: Num(c, "up_speed") * _opt.SpeedUnitBytes,
                DownBytesPerSec: Num(c, "down_speed") * _opt.SpeedUnitBytes));
        }
        return list;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";

    private static double Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => 0,
        };
    }

    private static string DecodeName(string name)
    {
        if (name.Length == 0) return name;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(name));
        }
        catch (FormatException) { return name; }
    }

    private void ClearAuth() { _stok = null; _cookie = null; _seq = 0; _passwordKey = null; _signKey = null; }

    private async Task LoginAsync(CancellationToken ct)
    {
        ClearAuth();
        _aesKey = DecoCrypto.RandomSixteenDigits();
        _aesIv = DecoCrypto.RandomSixteenDigits();

        var keys = await PostRawAsync("login", "keys", "{\"operation\":\"read\"}", ct);
        var pw = keys.GetProperty("result").GetProperty("password");
        _passwordKey = DecoCrypto.ParseKey(pw[0].GetString()!, pw[1].GetString()!);

        var auth = (await PostRawAsync("login", "auth", "{\"operation\":\"read\"}", ct)).GetProperty("result");
        var key = auth.GetProperty("key");
        _signKey = DecoCrypto.ParseKey(key[0].GetString()!, key[1].GetString()!);
        _seq = auth.GetProperty("seq").GetInt64();

        var encPassword = DecoCrypto.RsaEncryptHex(_passwordKey.Value, Encoding.UTF8.GetBytes(_opt.Password));
        var body = EncodeBody(new { @params = new { password = encPassword }, operation = "login" });
        var resp = await PostRawAsync("login", "login", body, ct);
        var data = Decrypt(resp);
        var errorCode = data.TryGetProperty("error_code", out var ec) ? ec.GetInt32() : -1;
        if (errorCode != 0)
        {
            var detail = data.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object ? " " + r.GetRawText() : "";
            var hint = errorCode switch
            {
                -5002 => "Wrong username or password.",
                -5003 => "Login is locked out after too many failed attempts; wait for the lockout to expire (or reboot the main Deco).",
                _ => "Check the credentials.",
            };
            throw new DecoAuthException($"Login failed (error_code={errorCode}{detail}). {hint}", errorCode);
        }
        _stok = data.GetProperty("result").GetProperty("stok").GetString();
        if (_cookie is null) throw new DecoApiException("Login response had no sysauth cookie.");
        _log.LogInformation("Logged in to Deco at {Host}", _baseUrl);
    }

    private async Task<JsonElement> CallAsync(string path, string form, object payload, CancellationToken ct)
    {
        var resp = await PostRawAsync($"{path}", form, EncodeBody(payload), ct, stok: _stok);
        var data = Decrypt(resp);
        if (data.TryGetProperty("error_code", out var ec) && ec.ValueKind == JsonValueKind.Number && ec.GetInt32() != 0)
            throw new DecoApiException($"{form} returned error_code={ec.GetInt32()}");
        return data.GetProperty("result");
    }

    private string EncodeBody(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var data = Convert.ToBase64String(DecoCrypto.AesEncrypt(Encoding.ASCII.GetBytes(_aesKey!), Encoding.ASCII.GetBytes(_aesIv!), Encoding.UTF8.GetBytes(json)));
        var signText = $"k={_aesKey}&i={_aesIv}&h={DecoCrypto.Md5Hex(_opt.Username + _opt.Password)}&s={_seq + data.Length}";
        var sign = DecoCrypto.RsaEncryptHex(_signKey!.Value, Encoding.ASCII.GetBytes(signText));
        return $"sign={sign}&data={Uri.EscapeDataString(data).Replace("%20", "+")}";
    }

    private JsonElement Decrypt(JsonElement response)
    {
        if (!response.TryGetProperty("data", out var d)) throw new DecoApiException("Response had no data field.");
        if (d.ValueKind == JsonValueKind.Object) return d;
        var s = d.GetString();
        if (string.IsNullOrEmpty(s)) throw new DecoApiException("Empty data (session expired?).");
        var plain = DecoCrypto.AesDecrypt(Encoding.ASCII.GetBytes(_aesKey!), Encoding.ASCII.GetBytes(_aesIv!), Convert.FromBase64String(s));
        return JsonDocument.Parse(plain).RootElement.Clone();
    }

    private async Task<JsonElement> PostRawAsync(string path, string form, string body, CancellationToken ct, string? stok = null)
    {
        for (var redirects = 0; ; redirects++)
        {
            var url = $"{_baseUrl}/cgi-bin/luci/;stok={stok}/{path}?form={form}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
            if (_cookie is not null) req.Headers.TryAddWithoutValidation("Cookie", _cookie);
            using var res = await _http.SendAsync(req, ct);

            if (res.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
                && redirects < 2)
            {
                // Typically http -> https on the same host; switch permanently so the cookie is sent.
                var uri = new Uri(_baseUrl);
                _baseUrl = $"https://{uri.Authority}";
                continue;
            }
            if (!res.IsSuccessStatusCode)
                throw new DecoApiException($"HTTP {(int)res.StatusCode} from {path}?form={form}");

            if (res.Headers.TryGetValues("Set-Cookie", out var cookies))
                foreach (var c in cookies)
                    if (SysAuth().Match(c) is { Success: true } m) { _cookie = m.Value; break; }

            return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement.Clone();
        }
    }

    [GeneratedRegex("sysauth=[a-f0-9]+")]
    private static partial Regex SysAuth();

    public void Dispose() { _http.Dispose(); _lock.Dispose(); }
}
