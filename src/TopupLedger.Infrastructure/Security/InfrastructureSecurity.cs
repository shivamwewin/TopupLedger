using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using TopupLedger.Application;
using TopupLedger.Application.Api;
using TopupLedger.Application.Persistence;
using TopupLedger.Application.Security;
using TopupLedger.Domain.Partners;

namespace TopupLedger.Infrastructure.Security;

public sealed class AesGcmSecretProtector : ISecretProtector
{
    private readonly byte[] _key;

    public AesGcmSecretProtector(byte[] key)
    {
        if (key.Length != 32)
        {
            throw new InvalidOperationException("DataProtection:KeyBase64 must decode to 32 bytes.");
        }

        _key = key;
    }

    public byte[] Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plain, cipher, tag);
        var packed = new byte[nonce.Length + tag.Length + cipher.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, nonce.Length);
        cipher.CopyTo(packed, nonce.Length + tag.Length);
        return packed;
    }

    public string Unprotect(byte[] protectedBytes)
    {
        var nonce = protectedBytes.AsSpan(0, 12);
        var tag = protectedBytes.AsSpan(12, 16);
        var cipher = protectedBytes.AsSpan(28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}

public sealed class PartnerAuthenticator : IPartnerAuthenticator
{
    private readonly ILedgerDbContext _db;
    private readonly ISecretProtector _protector;
    private readonly ILogger<PartnerAuthenticator> _logger;

    public PartnerAuthenticator(ILedgerDbContext db, ISecretProtector protector, ILogger<PartnerAuthenticator> logger)
    {
        _db = db;
        _protector = protector;
        _logger = logger;
    }

    public async Task<PartnerAuthResult> AuthenticateAsync(PartnerAuthRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey) || string.IsNullOrWhiteSpace(request.SignatureHex))
        {
            return RejectSignature();
        }

        var partner = await _db.Partners.AsNoTracking().SingleOrDefaultAsync(p => p.ApiKey == request.ApiKey, cancellationToken);
        if (partner is null)
        {
            _logger.LogInformation("Rejected partner request with an unknown API key.");
            return RejectSignature();
        }

        string secret;
        try
        {
            secret = _protector.Unprotect(partner.SecretProtected);
        }
        catch (CryptographicException)
        {
            _logger.LogError("Partner {PartnerId} secret could not be decrypted.", partner.Id);
            return RejectSignature();
        }

        if (!long.TryParse(request.Timestamp, out var unix) || request.Timestamp!.Length > 12)
        {
            return new PartnerAuthResult.Rejected(ApiResult.Problem(401, ErrorCodes.ClockSkew, "Authentication failed.", "Timestamp is outside the allowed window."));
        }

        var sent = DateTimeOffset.FromUnixTimeSeconds(unix);
        var skew = request.UtcNow - sent;
        if (skew.Duration() > TimeSpan.FromSeconds(300))
        {
            return new PartnerAuthResult.Rejected(ApiResult.Problem(401, ErrorCodes.ClockSkew, "Authentication failed.", "Timestamp is outside the allowed window."));
        }

        var idempotencyKey = request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : request.IdempotencyKey ?? string.Empty;
        var canonical = CanonicalSigner.Canonical(
            request.Method.ToUpperInvariant(),
            request.Path,
            request.Timestamp,
            idempotencyKey,
            CanonicalSigner.HashBody(request.RawBody));
        var expected = CanonicalSigner.Sign(secret, canonical);
        if (!CanonicalSigner.FixedTimeEqualsHex(expected, request.SignatureHex))
        {
            _logger.LogInformation("Rejected partner {PartnerId} because the signature did not match.", partner.Id);
            return RejectSignature();
        }

        if (request.IdempotencyKeyRequired && !Guid.TryParse(request.IdempotencyKey, out _))
        {
            return new PartnerAuthResult.Rejected(ApiResult.Problem(
                400,
                ErrorCodes.ValidationFailed,
                "The request was invalid.",
                errors: new Dictionary<string, string[]> { ["Idempotency-Key"] = ["Idempotency-Key must be a UUID."] }));
        }

        return new PartnerAuthResult.Ok(new AuthenticatedPartner(partner.Id, partner.Name, partner.Tier, partner.Status, partner.ParentPartnerId));
    }

    private static PartnerAuthResult.Rejected RejectSignature() =>
        new(ApiResult.Problem(401, ErrorCodes.InvalidSignature, "Authentication failed."));
}

public sealed class MySqlPartnerRateLimiter : IPartnerRateLimiter
{
    private readonly ILedgerDbContext _db;
    private readonly RateLimitRules _rules;

    public MySqlPartnerRateLimiter(ILedgerDbContext db, RateLimitRules rules)
    {
        _db = db;
        _rules = rules;
    }

    public async Task<RateLimitDecision> AcquireAsync(Guid partnerId, DateTime utcNow, CancellationToken cancellationToken)
    {
        const int attempts = 8;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await AcquireCoreAsync(partnerId, utcNow, cancellationToken);
            }
            catch (Exception ex) when (IsDeadlock(ex) && attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(15 * attempt), cancellationToken);
            }
        }
    }

    private async Task<RateLimitDecision> AcquireCoreAsync(Guid partnerId, DateTime utcNow, CancellationToken cancellationToken)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        var connection = (MySqlConnection)_db.Database.GetDbConnection();
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (MySqlTransaction?)_db.Database.CurrentTransaction!.GetDbTransaction();
            insert.CommandText = """
                INSERT INTO rate_buckets (partner_id, tokens, last_refill_utc)
                VALUES (@partnerId, @tokens, @now)
                ON DUPLICATE KEY UPDATE partner_id = partner_id
                """;
            insert.Parameters.Add(new MySqlParameter("@partnerId", partnerId));
            insert.Parameters.Add(new MySqlParameter("@tokens", _rules.Burst));
            insert.Parameters.Add(new MySqlParameter("@now", utcNow));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (MySqlTransaction?)_db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = "SELECT tokens, last_refill_utc FROM rate_buckets WHERE partner_id = @partnerId FOR UPDATE";
            command.Parameters.Add(new MySqlParameter("@partnerId", partnerId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            var tokens = reader.GetInt32(0);
            var last = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
            await reader.DisposeAsync();

            var elapsedMs = (utcNow - last).Ticks / TimeSpan.TicksPerMillisecond;
            if (elapsedMs < 0)
            {
                elapsedMs = 0;
            }

            var added = elapsedMs * _rules.PermitsPerSecond / 1000;
            if (added > 0)
            {
                tokens = (int)Math.Min(_rules.Burst, tokens + added);
                var consumedMs = added * 1000 / _rules.PermitsPerSecond;
                last = last.AddTicks(consumedMs * TimeSpan.TicksPerMillisecond);
            }

            var allowed = tokens > 0;
            if (allowed)
            {
                tokens--;
            }

            await using var update = connection.CreateCommand();
            update.Transaction = command.Transaction;
            update.CommandText = "UPDATE rate_buckets SET tokens = @tokens, last_refill_utc = @last WHERE partner_id = @partnerId";
            update.Parameters.Add(new MySqlParameter("@tokens", tokens));
            update.Parameters.Add(new MySqlParameter("@last", last));
            update.Parameters.Add(new MySqlParameter("@partnerId", partnerId));
            await update.ExecuteNonQueryAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return new RateLimitDecision(allowed, allowed ? 0 : 1);
        }
    }

    private static bool IsDeadlock(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is MySqlException mysql && mysql.Number is 1205 or 1213)
            {
                return true;
            }
        }

        return false;
    }
}

public static class PortalPassword
{
    public const int Iterations = 100_000;

    public static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);

    public static bool Verify(string password, byte[] salt, byte[] expected)
    {
        var actual = Hash(password, salt);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
