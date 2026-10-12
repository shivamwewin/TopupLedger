using TopupLedger.Application.Api;
using TopupLedger.Domain.Partners;

namespace TopupLedger.Application.Security;

public sealed record PartnerAuthRequest(
    string Method,
    string Path,
    string? ApiKey,
    string? Timestamp,
    string? SignatureHex,
    string? IdempotencyKey,
    byte[] RawBody,
    bool IdempotencyKeyRequired,
    DateTimeOffset UtcNow);

public sealed record AuthenticatedPartner(Guid Id, string Name, PartnerTier Tier, PartnerStatus Status, Guid? ParentPartnerId);

public abstract record PartnerAuthResult
{
    public sealed record Ok(AuthenticatedPartner Partner) : PartnerAuthResult;
    public sealed record Rejected(ApiResult Error) : PartnerAuthResult;
}

public interface IPartnerAuthenticator
{
    Task<PartnerAuthResult> AuthenticateAsync(PartnerAuthRequest request, CancellationToken cancellationToken);
}

public interface ISecretProtector
{
    byte[] Protect(string plaintext);
    string Unprotect(byte[] protectedBytes);
}

public interface IPartnerRateLimiter
{
    Task<RateLimitDecision> AcquireAsync(Guid partnerId, DateTime utcNow, CancellationToken cancellationToken);
}

public readonly record struct RateLimitDecision(bool Allowed, int RetryAfterSeconds);
