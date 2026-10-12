using TopupLedger.Domain.Msisdn;
using TopupLedger.Domain.Partners;
using TopupLedger.Domain.Topups;

namespace TopupLedger.Domain.Entities;

public static class AccountTypes
{
    public const string PartnerWallet = "PARTNER_WALLET";
    public const string HouseClearing = "HOUSE_CLEARING";
}

public sealed class Partner
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public PartnerTier Tier { get; set; }
    public PartnerStatus Status { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public byte[] SecretProtected { get; set; } = Array.Empty<byte>();
    public Guid? ParentPartnerId { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public Partner? Parent { get; set; }
    public Wallet? Wallet { get; set; }
}

public sealed class Account
{
    public Guid Id { get; set; }
    public Guid? PartnerId { get; set; }
    public string AccountType { get; set; } = string.Empty;
}

public sealed class Wallet
{
    public Guid PartnerId { get; set; }
    public Guid AccountId { get; set; }
    public long BalanceFils { get; set; }
    public Partner? Partner { get; set; }
}

public sealed class LedgerEntry
{
    public long Id { get; set; }
    public Guid TransactionId { get; set; }
    public Guid AccountId { get; set; }
    public long AmountFils { get; set; }
    public string EntryType { get; set; } = string.Empty;
    public Guid? TopupId { get; set; }
    public string? CorrelationId { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class Topup
{
    public Guid Id { get; set; }
    public Guid PartnerId { get; set; }
    public string ClientRef { get; set; } = string.Empty;
    public string Msisdn { get; set; } = string.Empty;
    public MobileOperator Operator { get; set; }
    public long FaceValueFils { get; set; }
    public long RetailerCommissionFils { get; set; }
    public long DistributorShareFils { get; set; }
    public long DebitFils { get; set; }
    public TopupStatus Status { get; set; }
    public DateOnly BusinessDay { get; set; }
    public Guid IdempotencyKey { get; set; }
    public int HighestEventSeq { get; set; }
    public string? OperatorTxnId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? DispatchedAtUtc { get; set; }
    public string? CorrelationId { get; set; }
}

public static class IdempotencyStates
{
    public const string InProgress = "IN_PROGRESS";
    public const string Completed = "COMPLETED";
}

public sealed class IdempotencyRecord
{
    public Guid PartnerId { get; set; }
    public Guid IdempotencyKey { get; set; }
    public string RequestHash { get; set; } = string.Empty;
    public string State { get; set; } = IdempotencyStates.InProgress;
    public int? HttpStatus { get; set; }
    public byte[]? ResponseBody { get; set; }
    public string? ContentType { get; set; }
    public string? Location { get; set; }
    public DateTime LockedUntilUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

public sealed class OperatorCallbackRecord
{
    public long Id { get; set; }
    public Guid TopupId { get; set; }
    public int EventSeq { get; set; }
    public string Status { get; set; } = string.Empty;
    public string OperatorTxnId { get; set; } = string.Empty;
    public bool Applied { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
}

public sealed class LateCallback
{
    public long Id { get; set; }
    public Guid TopupId { get; set; }
    public int EventSeq { get; set; }
    public string Status { get; set; } = string.Empty;
    public string OperatorTxnId { get; set; } = string.Empty;
    public DateTime ReceivedAtUtc { get; set; }
    public string Note { get; set; } = string.Empty;
}

public sealed class RateBucket
{
    public Guid PartnerId { get; set; }
    public int Tokens { get; set; }
    public DateTime LastRefillUtc { get; set; }
}

public static class SettlementStatuses
{
    public const string Queued = "QUEUED";
    public const string Running = "RUNNING";
    public const string Ready = "READY";
    public const string Failed = "FAILED";
}

public sealed class SettlementBatch
{
    public Guid Id { get; set; }
    public DateOnly BusinessDay { get; set; }
    public string Status { get; set; } = SettlementStatuses.Queued;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? Error { get; set; }
}

public sealed class SettlementLine
{
    public long Id { get; set; }
    public Guid BatchId { get; set; }
    public Guid PartnerId { get; set; }
    public int SalesCount { get; set; }
    public long FaceValueFils { get; set; }
    public long RetailerCommissionFils { get; set; }
    public long DistributorShareFils { get; set; }
    public long DebitFils { get; set; }
    public long ParentDistributorIncomeFils { get; set; }
}

public static class ReconciliationStatuses
{
    public const string Ready = "READY";
}

public sealed class ReconciliationRun
{
    public Guid Id { get; set; }
    public string FileSha256 { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Status { get; set; } = ReconciliationStatuses.Ready;
    public DateTime CreatedAtUtc { get; set; }
    public int MatchedCount { get; set; }
    public int MissingInOursCount { get; set; }
    public int MissingInOperatorCount { get; set; }
    public int AmountMismatchCount { get; set; }
    public int StatusMismatchCount { get; set; }
    public int DuplicateRowCount { get; set; }
}

public static class ReconciliationCategories
{
    public const string MissingInOurs = "MISSING_IN_OURS";
    public const string MissingInOperator = "MISSING_IN_OPERATOR";
    public const string AmountMismatch = "AMOUNT_MISMATCH";
    public const string StatusMismatch = "STATUS_MISMATCH";
}

public sealed class ReconciliationLine
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public string OperatorTxnId { get; set; } = string.Empty;
    public string Msisdn { get; set; } = string.Empty;
    public long AmountFils { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime EventTimeDubai { get; set; }
}

public sealed class ReconciliationFinding
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? OperatorTxnId { get; set; }
    public Guid? TopupId { get; set; }
    public string Detail { get; set; } = string.Empty;
}

public sealed class PortalUser
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public byte[] PasswordHash { get; set; } = Array.Empty<byte>();
    public byte[] PasswordSalt { get; set; } = Array.Empty<byte>();
    public string Role { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
