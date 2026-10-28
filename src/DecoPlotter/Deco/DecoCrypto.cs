using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace DecoPlotter.Deco;

internal static class DecoCrypto
{
    public static RSAParameters ParseKey(string nHex, string eHex) => new()
    {
        Modulus = HexToUnsignedBytes(nHex),
        Exponent = HexToUnsignedBytes(eHex),
    };

    private static byte[] HexToUnsignedBytes(string hex)
    {
        var value = BigInteger.Parse("0" + hex, System.Globalization.NumberStyles.HexNumber);
        return value.ToByteArray(isUnsigned: true, isBigEndian: true);
    }

    // TP-Link splits the plaintext into PKCS#1 v1.5 blocks and concatenates the hex of each ciphertext block.
    public static string RsaEncryptHex(RSAParameters key, byte[] plaintext)
    {
        using var rsa = RSA.Create();
        rsa.ImportParameters(key);
        var chunk = key.Modulus!.Length - 11;
        var sb = new StringBuilder();
        for (var i = 0; i < plaintext.Length; i += chunk)
        {
            var len = Math.Min(chunk, plaintext.Length - i);
            var enc = rsa.Encrypt(plaintext.AsSpan(i, len).ToArray(), RSAEncryptionPadding.Pkcs1);
            sb.Append(Convert.ToHexStringLower(enc));
        }
        return sb.ToString();
    }

    public static byte[] AesEncrypt(byte[] key, byte[] iv, byte[] plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptCbc(plaintext, iv, PaddingMode.PKCS7);
    }

    public static byte[] AesDecrypt(byte[] key, byte[] iv, byte[] ciphertext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(ciphertext, iv, PaddingMode.PKCS7);
    }

    // The router expects a 16-digit decimal number (no leading zero) used as ASCII key / IV.
    public static string RandomSixteenDigits()
    {
        var sb = new StringBuilder(16);
        sb.Append((char)('1' + RandomNumberGenerator.GetInt32(9)));
        for (var i = 1; i < 16; i++) sb.Append((char)('0' + RandomNumberGenerator.GetInt32(10)));
        return sb.ToString();
    }

    public static string Md5Hex(string s) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));
}
