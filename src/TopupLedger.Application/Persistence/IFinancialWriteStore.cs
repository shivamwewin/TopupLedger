using TopupLedger.Domain.Entities;
using TopupLedger.Domain.Msisdn;
using TopupLedger.Domain.Topups;

namespace TopupLedger.Application.Persistence;

public enum IdempotencyClaimKind
{
    Proceed,
    Replay,
    InProgress,
    KeyReused
}

public sealed record IdempotencyClaim(
    IdempotencyClaimKind Kind,
    int? HttpStatus,
    byte[]? Body,
    string? ContentType,
    string? Location);

public enum TopupWriteOutcome
{
    Created,
    InsufficientFunds,
    DuplicateClientRef,
    StoredReplay
}

public sealed record PendingTopupDraft(
    Guid TopupId,
    Guid PartnerId,
    Guid IdempotencyKey,
    string ClientRef,
    string Msisdn,
    MobileOperator Operator,
    long FaceValueFils,
    long RetailerCommissionFils,
    long DistributorShareFils,
    long DebitFils,
    DateOnly BusinessDay,
    DateTime CreatedAtUtc,
    string? CorrelationId,
    int HttpStatus,
    byte[] ResponseBody,
    string ContentType,
    string Location);

public sealed record TopupWriteResult(
    TopupWriteOutcome Outcome,
    Topup? Topup,
    int? HttpStatus = null,
    byte[]? Body = null,
    string? ContentType = null,
    string? Location = null);

public enum CallbackWriteKind
{
    Applied,
    Duplicate,
    Ignored,
    Late,
    NotFound
}

public sealed record CallbackWriteResult(CallbackWriteKind Kind, TopupStatus? Status);

public interface IFinancialWriteStore
{
    Task<IdempotencyClaim> ClaimIdempotencyAsync(
        Guid partnerId,
        Guid key,
        string requestHash,
        DateTime utcNow,
        TimeSpan lease,
        CancellationToken cancellationToken);

    Task CompleteIdempotencyAsync(
        Guid partnerId,
        Guid key,
        int httpStatus,
        byte[] body,
        string contentType,
        string? location,
        DateTime utcNow,
        CancellationToken cancellationToken);

    Task<TopupWriteResult> CreatePendingTopupAsync(PendingTopupDraft draft, CancellationToken cancellationToken);

    Task<CallbackWriteResult> ApplyCallbackAsync(
        Guid topupId,
        string status,
        int eventSeq,
        string operatorTxnId,
        DateTime utcNow,
        CancellationToken cancellationToken);

    Task<int> ExpirePendingAsync(DateTime utcNow, TimeSpan maxAge, int batchSize, CancellationToken cancellationToken);

    Task<IReadOnlyList<DispatchedTopup>> ClaimOperatorDispatchAsync(DateTime utcNow, int batchSize, CancellationToken cancellationToken);

    Task CreditWalletAsync(Guid partnerId, long amountFils, string reason, string? correlationId, DateTime utcNow, CancellationToken cancellationToken);

    Task<Guid> EnqueueSettlementAsync(DateOnly businessDay, DateTime utcNow, CancellationToken cancellationToken);

    Task<bool> TryRunNextSettlementAsync(DateTime utcNow, CancellationToken cancellationToken);
}

public sealed record DispatchedTopup(Guid TopupId, MobileOperator Operator);
