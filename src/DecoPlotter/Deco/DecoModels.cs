namespace DecoPlotter.Deco;

public sealed record DecoClientInfo(
    string Mac,
    string Name,
    string Ip,
    bool Online,
    string ConnectionType,
    string Interface,
    string? AccessPoint,
    double UpBytesPerSec,
    double DownBytesPerSec);

public class DecoApiException(string message) : Exception(message);

/// <summary>Login was rejected. Must not be retried quickly: the router locks out after repeated failures.</summary>
public sealed class DecoAuthException(string message, int errorCode) : DecoApiException(message)
{
    public int ErrorCode { get; } = errorCode;
}
