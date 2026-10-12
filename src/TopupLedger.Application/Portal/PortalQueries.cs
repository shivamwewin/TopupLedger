using MediatR;
using Microsoft.EntityFrameworkCore;
using TopupLedger.Application.Persistence;
using TopupLedger.Domain.Entities;
using TopupLedger.Domain.Money;
using TopupLedger.Domain.Msisdn;
using TopupLedger.Domain.Partners;
using TopupLedger.Domain.Time;
using TopupLedger.Domain.Topups;

namespace TopupLedger.Application.Portal;

public sealed record DashboardModel(
    DateOnly BusinessDay,
    long TotalFloatFils,
    long ClearingFils,
    IReadOnlyList<DashboardSlice> ByStatus,
    IReadOnlyList<DashboardSlice> ByOperator);

public sealed record DashboardSlice(string Key, int Count, long FaceValueFils);

public sealed record GetDashboardQuery : IRequest<DashboardModel>;

public sealed class GetDashboardHandler : IRequestHandler<GetDashboardQuery, DashboardModel>
{
    private readonly ILedgerDbContext _db;
    private readonly LedgerRules _rules;
    private readonly TimeProvider _clock;

    public GetDashboardHandler(ILedgerDbContext db, LedgerRules rules, TimeProvider clock)
    {
        _db = db;
        _rules = rules;
        _clock = clock;
    }

    public async Task<DashboardModel> Handle(GetDashboardQuery request, CancellationToken cancellationToken)
    {
        var today = BusinessDayCalculator.ForInstant(_clock.GetUtcNow(), _rules.BusinessDayCutoff);
        var rows = await _db.Topups.AsNoTracking()
            .Where(t => t.BusinessDay == today)
            .Select(t => new { t.Status, t.Operator, t.FaceValueFils })
            .ToListAsync(cancellationToken);
        var byStatus = rows.GroupBy(r => TopupStatusNames.ToApi(r.Status))
            .Select(g => new DashboardSlice(g.Key, g.Count(), g.Sum(x => x.FaceValueFils)))
            .OrderBy(s => s.Key)
            .ToList();
        var byOperator = rows.GroupBy(r => MobileOperatorParser.ToApi(r.Operator))
            .Select(g => new DashboardSlice(g.Key, g.Count(), g.Sum(x => x.FaceValueFils)))
            .OrderBy(s => s.Key)
            .ToList();
        var floatFils = await _db.Wallets.SumAsync(w => (long?)w.BalanceFils, cancellationToken) ?? 0;
        var clearing = await _db.LedgerEntries.AsNoTracking()
            .Where(e => _db.Accounts.Any(a => a.Id == e.AccountId && a.AccountType == AccountTypes.HouseClearing))
            .SumAsync(e => (long?)e.AmountFils, cancellationToken) ?? 0;
        return new DashboardModel(today, floatFils, clearing, byStatus, byOperator);
    }
}

public sealed record PartnerListItem(Guid Id, string Name, string Tier, string Status, string ApiKey, long BalanceFils, string? ParentName);

public sealed record ListPartnersQuery : IRequest<IReadOnlyList<PartnerListItem>>;

public sealed class ListPartnersHandler : IRequestHandler<ListPartnersQuery, IReadOnlyList<PartnerListItem>>
{
    private readonly ILedgerDbContext _db;

    public ListPartnersHandler(ILedgerDbContext db) => _db = db;

    public async Task<IReadOnlyList<PartnerListItem>> Handle(ListPartnersQuery request, CancellationToken cancellationToken)
    {
        return await _db.Partners.AsNoTracking()
            .OrderBy(p => p.Name)
            .Select(p => new PartnerListItem(
                p.Id,
                p.Name,
                p.Tier == PartnerTier.Distributor ? "DISTRIBUTOR" : "RETAILER",
                p.Status == PartnerStatus.Active ? "ACTIVE" : "SUSPENDED",
                p.ApiKey,
                p.Wallet!.BalanceFils,
                p.Parent != null ? p.Parent.Name : null))
            .ToListAsync(cancellationToken);
    }
}

public sealed record TransactionSearchQuery(
    string? Msisdn,
    string? ClientRef,
    string? Status,
    DateTime? FromUtc,
    DateTime? ToUtc,
    string? Cursor) : IRequest<TransactionSearchPage>;

public sealed record TransactionSearchRow(
    Guid Id,
    string PartnerName,
    string ClientRef,
    string Msisdn,
    string Operator,
    string Status,
    long FaceValueFils,
    DateOnly BusinessDay,
    DateTime CreatedAtUtc);

public sealed record TransactionSearchPage(IReadOnlyList<TransactionSearchRow> Items, string? NextCursor);

public sealed class TransactionSearchHandler : IRequestHandler<TransactionSearchQuery, TransactionSearchPage>
{
    private readonly ILedgerDbContext _db;

    public TransactionSearchHandler(ILedgerDbContext db) => _db = db;

    public async Task<TransactionSearchPage> Handle(TransactionSearchQuery query, CancellationToken cancellationToken)
    {
        var q = _db.Topups.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Msisdn))
        {
            var msisdn = MsisdnNormalizer.TryNormalize(query.Msisdn, out var normalized, out _)
                ? normalized.Value
                : query.Msisdn.Trim();
            q = q.Where(t => t.Msisdn == msisdn);
        }

        if (!string.IsNullOrWhiteSpace(query.ClientRef))
        {
            q = q.Where(t => t.ClientRef == query.ClientRef.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query.Status) && TopupStatusNames.TryParse(query.Status, out var status))
        {
            q = q.Where(t => t.Status == status);
        }

        if (query.FromUtc is not null)
        {
            q = q.Where(t => t.CreatedAtUtc >= query.FromUtc);
        }

        if (query.ToUtc is not null)
        {
            q = q.Where(t => t.CreatedAtUtc < query.ToUtc);
        }

        if (!string.IsNullOrWhiteSpace(query.Cursor) && Topups.KeysetCursor.TryDecode(query.Cursor, out var created, out var id))
        {
            var idText = id.ToString("D");
            q = q.Where(t => t.CreatedAtUtc < created || (t.CreatedAtUtc == created && t.Id.ToString().CompareTo(idText) < 0));
        }

        var rows = await q
            .OrderByDescending(t => t.CreatedAtUtc)
            .ThenByDescending(t => t.Id)
            .Take(21)
            .Join(_db.Partners.AsNoTracking(), t => t.PartnerId, p => p.Id, (t, p) => new { t, p.Name })
            .Select(x => new TransactionSearchRow(
                x.t.Id,
                x.Name,
                x.t.ClientRef,
                x.t.Msisdn,
                x.t.Operator == MobileOperator.Etisalat ? "ETISALAT" : "DU",
                x.t.Status == TopupStatus.Pending ? "PENDING" :
                x.t.Status == TopupStatus.Success ? "SUCCESS" :
                x.t.Status == TopupStatus.Failed ? "FAILED" : "EXPIRED",
                x.t.FaceValueFils,
                x.t.BusinessDay,
                x.t.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        string? next = null;
        if (rows.Count == 21)
        {
            next = Topups.KeysetCursor.Encode(rows[19].CreatedAtUtc, rows[19].Id);
            rows.RemoveAt(20);
        }

        return new TransactionSearchPage(rows, next);
    }
}

public sealed record TopupDetailModel(
    TransactionSearchRow Topup,
    long DebitFils,
    long RetailerCommissionFils,
    long DistributorShareFils,
    string? OperatorTxnId,
    IReadOnlyList<LedgerItem> Ledger,
    IReadOnlyList<CallbackItem> Callbacks,
    IReadOnlyList<CallbackItem> LateCallbacks);

public sealed record LedgerItem(long Id, Guid TransactionId, long AmountFils, string EntryType, string? Reason, DateTime CreatedAtUtc);

public sealed record CallbackItem(int EventSeq, string Status, string OperatorTxnId, bool Applied, DateTime ReceivedAtUtc, string? Note);

public sealed record GetTopupDetailQuery(Guid Id) : IRequest<TopupDetailModel?>;

public sealed class GetTopupDetailHandler : IRequestHandler<GetTopupDetailQuery, TopupDetailModel?>
{
    private readonly ILedgerDbContext _db;

    public GetTopupDetailHandler(ILedgerDbContext db) => _db = db;

    public async Task<TopupDetailModel?> Handle(GetTopupDetailQuery query, CancellationToken cancellationToken)
    {
        var topup = await _db.Topups.AsNoTracking().SingleOrDefaultAsync(t => t.Id == query.Id, cancellationToken);
        if (topup is null)
        {
            return null;
        }

        var partnerName = await _db.Partners.AsNoTracking().Where(p => p.Id == topup.PartnerId).Select(p => p.Name).SingleAsync(cancellationToken);
        var ledger = await _db.LedgerEntries.AsNoTracking()
            .Where(e => e.TopupId == topup.Id)
            .OrderBy(e => e.Id)
            .Select(e => new LedgerItem(e.Id, e.TransactionId, e.AmountFils, e.EntryType, e.Reason, e.CreatedAtUtc))
            .ToListAsync(cancellationToken);
        var callbacks = await _db.OperatorCallbacks.AsNoTracking()
            .Where(c => c.TopupId == topup.Id)
            .OrderBy(c => c.EventSeq)
            .Select(c => new CallbackItem(c.EventSeq, c.Status, c.OperatorTxnId, c.Applied, c.ReceivedAtUtc, null))
            .ToListAsync(cancellationToken);
        var late = await _db.LateCallbacks.AsNoTracking()
            .Where(c => c.TopupId == topup.Id)
            .OrderBy(c => c.EventSeq)
            .Select(c => new CallbackItem(c.EventSeq, c.Status, c.OperatorTxnId, false, c.ReceivedAtUtc, c.Note))
            .ToListAsync(cancellationToken);
        var row = new TransactionSearchRow(
            topup.Id,
            partnerName,
            topup.ClientRef,
            topup.Msisdn,
            MobileOperatorParser.ToApi(topup.Operator),
            TopupStatusNames.ToApi(topup.Status),
            topup.FaceValueFils,
            topup.BusinessDay,
            topup.CreatedAtUtc);
        return new TopupDetailModel(row, topup.DebitFils, topup.RetailerCommissionFils, topup.DistributorShareFils, topup.OperatorTxnId, ledger, callbacks, late);
    }
}

public sealed record SettlementProgress(Guid Id, DateOnly BusinessDay, string Status, string? Error, int LineCount);

public sealed record GetSettlementProgressQuery(DateOnly BusinessDay) : IRequest<SettlementProgress?>;

public sealed class GetSettlementProgressHandler : IRequestHandler<GetSettlementProgressQuery, SettlementProgress?>
{
    private readonly ILedgerDbContext _db;

    public GetSettlementProgressHandler(ILedgerDbContext db) => _db = db;

    public async Task<SettlementProgress?> Handle(GetSettlementProgressQuery query, CancellationToken cancellationToken)
    {
        var batch = await _db.SettlementBatches.AsNoTracking().SingleOrDefaultAsync(b => b.BusinessDay == query.BusinessDay, cancellationToken);
        if (batch is null)
        {
            return null;
        }

        var count = await _db.SettlementLines.CountAsync(l => l.BatchId == batch.Id, cancellationToken);
        return new SettlementProgress(batch.Id, batch.BusinessDay, batch.Status, batch.Error, count);
    }
}

public static class MoneyText
{
    public static string Aed(long fils) => Fils.FormatAed(fils);
}
