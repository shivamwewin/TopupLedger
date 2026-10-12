using System.Security.Cryptography;
using System.Text;

namespace TopupLedger.Application.Security;

/// <summary>
/// HMAC-SHA256 over the canonical string. The body hash is SHA-256 of the raw bytes,
/// never of a re-serialised document.
/// </summary>
public static class CanonicalSigner
{
    public static string HashBody(ReadOnlySpan<byte> rawBody)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(rawBody, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Canonical(string method, string path, string unixTimestamp, string idempotencyKey, string bodyHashHex) =>
        string.Join('\n', method, path, unixTimestamp, idempotencyKey, bodyHashHex);

    public static byte[] Sign(string secret, string canonical)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.UTF8.GetBytes(canonical);
        return HMACSHA256.HashData(key, data);
    }

    public static bool FixedTimeEqualsHex(byte[] expected, string? providedHex)
    {
        if (string.IsNullOrWhiteSpace(providedHex) || !TryDecodeHex(providedHex.Trim(), out var provided))
        {
            var dummy = new byte[expected.Length];
            CryptographicOperations.FixedTimeEquals(expected, dummy);
            return false;
        }

        if (provided.Length != expected.Length)
        {
            CryptographicOperations.FixedTimeEquals(expected, expected);
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    public static bool TryDecodeHex(string hex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (hex.Length == 0 || (hex.Length & 1) == 1)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(hex);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
