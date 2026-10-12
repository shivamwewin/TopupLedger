using System.Globalization;
using MediatR;
using Microsoft.EntityFrameworkCore;
using TopupLedger.Application.Api;
using TopupLedger.Application.Persistence;
using TopupLedger.Domain.Entities;
using TopupLedger.Domain.Topups;

namespace TopupLedger.Application.Topups;

public sealed class GetTopupHandler : IRequestHandler<GetTopupQuery, ApiResult>
{
    private readonly ILedgerDbContext _db;

    public GetTopupHandler(ILedgerDbContext db) => _db = db;

    public async Task<ApiResult> Handle(GetTopupQuery query, CancellationToken cancellationToken)
    {
        var topup = await _db.Topups.AsNoTracking().SingleOrDefaultAsync(t => t.Id == query.TopupId, cancellationToken);
        if (topup is null)
        {
            return ApiResult.Problem(404, ErrorCodes.NotFound, "Top-up was not found.");
        }

        if (topup.PartnerId != query.PartnerId)
        {
            return ApiResult.Problem(403, "FORBIDDEN", "You cannot view another partner's top-up.");
        }

        return ApiResult.Json(200, TopupBodies.From(topup));
    }
}

public sealed class ListTopupsHandler : IRequestHandler<ListTopupsQuery, ApiResult>
{
    private readonly ILedgerDbContext _db;

    public ListTopupsHandler(ILedgerDbContext db) => _db = db;

    public async Task<ApiResult> Handle(ListTopupsQuery query, CancellationToken cancellationToken)
    {
        if (query.OffsetRequested)
        {
            return ApiResult.Problem(
                400,
                ErrorCodes.ValidationFailed,
                "The request was invalid.",
                errors: new Dictionary<string, string[]> { ["offset"] = ["Offset pagination is not supported. Use cursor."] });
        }

        var limit = query.Limit ?? 20;
        if (limit is < 1 or > 100)
        {
            return ApiResult.Problem(
                400,
                ErrorCodes.ValidationFailed,
                "The request was invalid.",
                errors: new Dictionary<string, string[]> { ["limit"] = ["limit must be between 1 and 100."] });
        }

        TopupStatus? status = null;
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!TopupStatusNames.TryParse(query.Status, out var parsed))
            {
                return ApiResult.Problem(
                    400,
                    ErrorCodes.ValidationFailed,
                    "The request was invalid.",
                    errors: new Dictionary<string, string[]> { ["status"] = ["status must be PENDING, SUCCESS, FAILED, or EXPIRED."] });
            }

            status = parsed;
        }

        var fromOk = TryBound(query.From, inclusiveDate: true, out var from, out var fromError);
        var toOk = TryBound(query.To, inclusiveDate: false, out var to, out var toError);
        if (!fromOk || !toOk)
        {
            var errors = new Dictionary<string, string[]>();
            if (fromError is not null)
            {
                errors["from"] = [fromError];
            }

            if (toError is not null)
            {
                errors["to"] = [toError];
            }

            return ApiResult.Problem(400, ErrorCodes.ValidationFailed, "The request was invalid.", errors: errors);
        }

        (DateTime CreatedAtUtc, Guid Id)? cursor = null;
        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            if (!KeysetCursor.TryDecode(query.Cursor, out var created, out var id))
            {
                return ApiResult.Problem(
                    400,
                    ErrorCodes.ValidationFailed,
                    "The request was invalid.",
                    errors: new Dictionary<string, string[]> { ["cursor"] = ["cursor is not valid."] });
            }

            cursor = (created, id);
        }

        var q = _db.Topups.AsNoTracking().Where(t => t.PartnerId == query.PartnerId);
        if (status is not null)
        {
            q = q.Where(t => t.Status == status);
        }

        if (from is not null)
        {
            q = q.Where(t => t.CreatedAtUtc >= from);
        }

        if (to is not null)
        {
            q = q.Where(t => t.CreatedAtUtc < to);
        }

        if (cursor is not null)
        {
            var created = cursor.Value.CreatedAtUtc;
            var idText = cursor.Value.Id.ToString("D");
            q = q.Where(t => t.CreatedAtUtc < created || (t.CreatedAtUtc == created && t.Id.ToString().CompareTo(idText) < 0));
        }

        var rows = await q
            .OrderByDescending(t => t.CreatedAtUtc)
            .ThenByDescending(t => t.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        string? next = null;
        if (rows.Count > limit)
        {
            var last = rows[limit - 1];
            next = KeysetCursor.Encode(last.CreatedAtUtc, last.Id);
            rows.RemoveAt(rows.Count - 1);
        }

        return ApiResult.Json(200, new TopupListBody(rows.Select(TopupBodies.From).ToList(), next));
    }

    private static bool TryBound(string? text, bool inclusiveDate, out DateTime? instant, out string? error)
    {
        instant = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto) &&
            text.Contains('T', StringComparison.Ordinal))
        {
            instant = dto.UtcDateTime;
            return true;
        }

        if (DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            var day = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            instant = inclusiveDate ? day : day;
            if (!inclusiveDate)
            {
                instant = day.AddDays(1);
            }

            return true;
        }

        error = "Use a UTC date (YYYY-MM-DD) or an ISO-8601 timestamp.";
        return false;
    }
}

public static class KeysetCursor
{
    public static string Encode(DateTime createdAtUtc, Guid id)
    {
        var raw = $"{createdAtUtc:O}|{id:D}";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string cursor, out DateTime createdAtUtc, out Guid id)
    {
        createdAtUtc = default;
        id = default;
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var split = text.Split('|', 2);
            if (split.Length != 2)
            {
                return false;
            }

            if (!DateTime.TryParse(split[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out createdAtUtc))
            {
                return false;
            }

            createdAtUtc = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc);
            return Guid.TryParse(split[1], out id);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class GetWalletHandler : IRequestHandler<GetWalletQuery, ApiResult>
{
    private readonly ILedgerDbContext _db;

    public GetWalletHandler(ILedgerDbContext db) => _db = db;

    public async Task<ApiResult> Handle(GetWalletQuery query, CancellationToken cancellationToken)
    {
        var wallet = await _db.Wallets.AsNoTracking().SingleAsync(w => w.PartnerId == query.PartnerId, cancellationToken);
        var entries = await _db.LedgerEntries.AsNoTracking()
            .Where(e => e.AccountId == wallet.AccountId)
            .OrderByDescending(e => e.Id)
            .Take(20)
            .Select(e => new LedgerItemBody(e.Id, e.TransactionId, e.AmountFils, e.EntryType, e.TopupId, e.Reason, e.CreatedAtUtc))
            .ToListAsync(cancellationToken);
        return ApiResult.Json(200, new WalletBody(wallet.BalanceFils, "AED", entries));
    }
}

public sealed class GetSettlementHandler : IRequestHandler<GetSettlementQuery, ApiResult>
{
    private readonly ILedgerDbContext _db;

    public GetSettlementHandler(ILedgerDbContext db) => _db = db;

    public async Task<ApiResult> Handle(GetSettlementQuery query, CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact(query.BusinessDay, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return ApiResult.Problem(
                400,
                ErrorCodes.ValidationFailed,
                "The request was invalid.",
                errors: new Dictionary<string, string[]> { ["businessDay"] = ["Use YYYY-MM-DD."] });
        }

        var batch = await _db.SettlementBatches.AsNoTracking()
            .SingleOrDefaultAsync(b => b.BusinessDay == day && b.Status == SettlementStatuses.Ready, cancellationToken);
        if (batch is null)
        {
            return ApiResult.Problem(404, ErrorCodes.NotFound, "Settlement has not been generated for this business day.");
        }

        var line = await _db.SettlementLines.AsNoTracking()
            .SingleOrDefaultAsync(l => l.BatchId == batch.Id && l.PartnerId == query.PartnerId, cancellationToken);
        var body = new SettlementBody(
            day.ToString("yyyy-MM-dd"),
            line?.SalesCount ?? 0,
            line?.FaceValueFils ?? 0,
            line?.RetailerCommissionFils ?? 0,
            line?.DistributorShareFils ?? 0,
            line?.DebitFils ?? 0,
            line?.ParentDistributorIncomeFils ?? 0);
        return ApiResult.Json(200, body);
    }
}
