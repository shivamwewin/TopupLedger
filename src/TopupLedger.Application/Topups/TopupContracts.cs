using MediatR;
using TopupLedger.Application.Api;
using TopupLedger.Domain.Msisdn;
using TopupLedger.Domain.Topups;

namespace TopupLedger.Application.Topups;

public sealed record CreateTopupCommand(Guid PartnerId, byte[] RawBody, Guid IdempotencyKey, string? CorrelationId) : IRequest<ApiResult>;

public sealed record GetTopupQuery(Guid PartnerId, Guid TopupId) : IRequest<ApiResult>;

public sealed record ListTopupsQuery(
    Guid PartnerId,
    string? Status,
    string? From,
    string? To,
    int? Limit,
    string? Cursor,
    bool OffsetRequested) : IRequest<ApiResult>;

public sealed record GetWalletQuery(Guid PartnerId) : IRequest<ApiResult>;

public sealed record GetSettlementQuery(Guid PartnerId, string BusinessDay) : IRequest<ApiResult>;

public sealed record ApplyOperatorCallbackCommand(
    MobileOperator Operator,
    string? OperatorToken,
    string? OperatorTxnId,
    Guid TopupId,
    string? Status,
    int EventSeq) : IRequest<ApiResult>;

public sealed record TopupBody(
    Guid Id,
    string Status,
    string ClientRef,
    string Msisdn,
    string Operator,
    long FaceValueFils,
    long RetailerCommissionFils,
    long DistributorShareFils,
    long DebitFils,
    string BusinessDay,
    DateTime CreatedAtUtc);

public static class TopupBodies
{
    public static TopupBody From(Domain.Entities.Topup topup) => new(
        topup.Id,
        TopupStatusNames.ToApi(topup.Status),
        topup.ClientRef,
        topup.Msisdn,
        MobileOperatorParser.ToApi(topup.Operator),
        topup.FaceValueFils,
        topup.RetailerCommissionFils,
        topup.DistributorShareFils,
        topup.DebitFils,
        topup.BusinessDay.ToString("yyyy-MM-dd"),
        DateTime.SpecifyKind(topup.CreatedAtUtc, DateTimeKind.Utc));
}

public sealed record WalletBody(long BalanceFils, string Currency, IReadOnlyList<LedgerItemBody> Entries);

public sealed record LedgerItemBody(
    long Id,
    Guid TransactionId,
    long AmountFils,
    string EntryType,
    Guid? TopupId,
    string? Reason,
    DateTime CreatedAtUtc);

public sealed record TopupListBody(IReadOnlyList<TopupBody> Items, string? NextCursor);

public sealed record SettlementBody(
    string BusinessDay,
    int SalesCount,
    long FaceValueFils,
    long RetailerCommissionFils,
    long DistributorShareFils,
    long DebitFils,
    long ParentDistributorIncomeFils);
