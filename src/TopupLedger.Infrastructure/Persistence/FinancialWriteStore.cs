using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MySqlConnector;
using TopupLedger.Application;
using TopupLedger.Application.Persistence;
using TopupLedger.Application.Topups;
using TopupLedger.Domain.Entities;
using TopupLedger.Domain.Ledger;
using TopupLedger.Domain.Topups;

namespace TopupLedger.Infrastructure.Persistence;

/// <summary>
/// Financial writes take row locks inside InnoDB transactions. A conditional
/// UPDATE is what stops two debits from both spending the last fils. In-memory
/// locks are intentionally absent so a second API instance sees the same guard.
/// </summary>
public sealed class FinancialWriteStore : IFinancialWriteStore
{
    private readonly LedgerDbContext _db;
    private readonly TopupTiming _timing;

    public FinancialWriteStore(LedgerDbContext db, TopupTiming timing)
    {
        _db = db;
        _timing = timing;
    }

    public Task<IdempotencyClaim> ClaimIdempotencyAsync(
        Guid partnerId,
        Guid key,
        string requestHash,
        DateTime utcNow,
        TimeSpan lease,
        CancellationToken cancellationToken) =>
        RetryAsync(
            ct => ClaimIdempotencyCoreAsync(partnerId, key, requestHash, utcNow, lease, ct),
            cancellationToken);

    private async Task<IdempotencyClaim> ClaimIdempotencyCoreAsync(
        Guid partnerId,
        Guid key,
        string requestHash,
        DateTime utcNow,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        var lockedUntil = utcNow.Add(lease);
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        _db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            PartnerId = partnerId,
            IdempotencyKey = key,
            RequestHash = requestHash,
            State = IdempotencyStates.InProgress,
            LockedUntilUtc = lockedUntil,
            CreatedAtUtc = utcNow
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return new IdempotencyClaim(IdempotencyClaimKind.Proceed, null, null, null, null);
        }
        catch (DbUpdateException ex) when (IsDuplicate(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            _db.ChangeTracker.Clear();
        }

        await using var readTx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await LockAsync(
            "SELECT partner_id FROM idempotency_records WHERE partner_id = @partnerId AND idempotency_key = @idemKey FOR UPDATE",
            [P("@partnerId", partnerId), P("@idemKey", key)],
            cancellationToken);

        var row = await _db.IdempotencyRecords.SingleAsync(
            r => r.PartnerId == partnerId && r.IdempotencyKey == key,
            cancellationToken);

        if (row.CreatedAtUtc <= utcNow.Subtract(_timing.IdempotencyTtl))
        {
            row.RequestHash = requestHash;
            row.State = IdempotencyStates.InProgress;
            row.HttpStatus = null;
            row.ResponseBody = null;
            row.ContentType = null;
            row.Location = null;
            row.CompletedAtUtc = null;
            row.LockedUntilUtc = lockedUntil;
            row.CreatedAtUtc = utcNow;
            await _db.SaveChangesAsync(cancellationToken);
            await readTx.CommitAsync(cancellationToken);
            return new IdempotencyClaim(IdempotencyClaimKind.Proceed, null, null, null, null);
        }

        if (!string.Equals(row.RequestHash, requestHash, StringComparison.Ordinal))
        {
            await readTx.CommitAsync(cancellationToken);
            return new IdempotencyClaim(IdempotencyClaimKind.KeyReused, null, null, null, null);
        }

        if (row.State == IdempotencyStates.Completed && row.HttpStatus is not null && row.ResponseBody is not null && row.ContentType is not null)
        {
            var replay = new IdempotencyClaim(IdempotencyClaimKind.Replay, row.HttpStatus, row.ResponseBody, row.ContentType, row.Location);
            await readTx.CommitAsync(cancellationToken);
            return replay;
        }

        if (row.LockedUntilUtc > utcNow)
        {
            await readTx.CommitAsync(cancellationToken);
            return new IdempotencyClaim(IdempotencyClaimKind.InProgress, null, null, null, null);
        }

        row.LockedUntilUtc = lockedUntil;
        row.CreatedAtUtc = utcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await readTx.CommitAsync(cancellationToken);
        return new IdempotencyClaim(IdempotencyClaimKind.Proceed, null, null, null, null);
    }

    public async Task CompleteIdempotencyAsync(
        Guid partnerId,
        Guid key,
        int httpStatus,
        byte[] body,
        string contentType,
        string? location,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var row = await _db.IdempotencyRecords.SingleAsync(
            r => r.PartnerId == partnerId && r.IdempotencyKey == key,
            cancellationToken);
        row.State = IdempotencyStates.Completed;
        row.HttpStatus = httpStatus;
        row.ResponseBody = body;
        row.ContentType = contentType;
        row.Location = location;
        row.CompletedAtUtc = utcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<TopupWriteResult> CreatePendingTopupAsync(PendingTopupDraft draft, CancellationToken cancellationToken) =>
        RetryAsync(ct => CreatePendingTopupCoreAsync(draft, ct), cancellationToken);

    private async Task<TopupWriteResult> CreatePendingTopupCoreAsync(PendingTopupDraft draft, CancellationToken cancellationToken)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await LockAsync(
            "SELECT partner_id FROM idempotency_records WHERE partner_id = @partnerId AND idempotency_key = @idemKey FOR UPDATE",
            [P("@partnerId", draft.PartnerId), P("@idemKey", draft.IdempotencyKey)],
            cancellationToken);

        var existingId = await QueryGuidAsync(
            "SELECT id FROM topups WHERE partner_id = @partnerId AND idempotency_key = @idemKey FOR UPDATE",
            [P("@partnerId", draft.PartnerId), P("@idemKey", draft.IdempotencyKey)],
            cancellationToken);

        if (existingId is Guid already)
        {
            var replay = await FinishReplayAsync(draft.PartnerId, draft.IdempotencyKey, already, draft.CreatedAtUtc, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return replay;
        }

        var houseId = await _db.Accounts.AsNoTracking()
            .Where(a => a.AccountType == AccountTypes.HouseClearing)
            .Select(a => a.Id)
            .SingleAsync(cancellationToken);
        var walletAccountId = await _db.Wallets.AsNoTracking()
            .Where(w => w.PartnerId == draft.PartnerId)
            .Select(w => w.AccountId)
            .SingleAsync(cancellationToken);

        var debited = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE wallets SET balance_fils = balance_fils - {draft.DebitFils} WHERE partner_id = {draft.PartnerId} AND balance_fils >= {draft.DebitFils}",
            cancellationToken);
        if (debited != 1)
        {
            await tx.RollbackAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            return new TopupWriteResult(TopupWriteOutcome.InsufficientFunds, null);
        }

        var (partnerAmount, houseAmount) = LedgerPair.Split(draft.DebitFils);
        var movement = Guid.NewGuid();
        var topup = new Topup
        {
            Id = draft.TopupId,
            PartnerId = draft.PartnerId,
            ClientRef = draft.ClientRef,
            Msisdn = draft.Msisdn,
            Operator = draft.Operator,
            FaceValueFils = draft.FaceValueFils,
            RetailerCommissionFils = draft.RetailerCommissionFils,
            DistributorShareFils = draft.DistributorShareFils,
            DebitFils = draft.DebitFils,
            Status = TopupStatus.Pending,
            BusinessDay = draft.BusinessDay,
            IdempotencyKey = draft.IdempotencyKey,
            CreatedAtUtc = draft.CreatedAtUtc,
            CorrelationId = draft.CorrelationId
        };
        _db.Topups.Add(topup);
        AddPair(movement, walletAccountId, partnerAmount, LedgerEntryTypes.TopupDebit, houseId, houseAmount, LedgerEntryTypes.TopupDebitContra, draft.TopupId, draft.CorrelationId, null, draft.CreatedAtUtc);

        var idem = await _db.IdempotencyRecords.SingleAsync(
            r => r.PartnerId == draft.PartnerId && r.IdempotencyKey == draft.IdempotencyKey,
            cancellationToken);
        StampCompleted(idem, draft.HttpStatus, draft.ResponseBody, draft.ContentType, draft.Location, draft.CreatedAtUtc);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return new TopupWriteResult(TopupWriteOutcome.Created, topup);
        }
        catch (DbUpdateException ex) when (IsDuplicate(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            var message = ex.InnerException?.Message ?? ex.Message;
            if (message.Contains("uk_topups_partner_client", StringComparison.OrdinalIgnoreCase))
            {
                return new TopupWriteResult(TopupWriteOutcome.DuplicateClientRef, null);
            }

            var raced = await _db.Topups.AsNoTracking().SingleOrDefaultAsync(
                t => t.PartnerId == draft.PartnerId && t.IdempotencyKey == draft.IdempotencyKey,
                cancellationToken);
            if (raced is null)
            {
                throw;
            }

            return await ReadStoredReplayAsync(draft.PartnerId, draft.IdempotencyKey, raced, cancellationToken);
        }
    }

    public async Task<CallbackWriteResult> ApplyCallbackAsync(
        Guid topupId,
        string status,
        int eventSeq,
        string operatorTxnId,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var locked = await QueryGuidAsync(
            "SELECT id FROM topups WHERE id = @id FOR UPDATE",
            [P("@id", topupId)],
            cancellationToken);
        if (locked is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return new CallbackWriteResult(CallbackWriteKind.NotFound, null);
        }

        var topup = await _db.Topups.SingleAsync(t => t.Id == topupId, cancellationToken);
        var already = await _db.OperatorCallbacks.AnyAsync(
            c => c.TopupId == topupId && c.EventSeq == eventSeq,
            cancellationToken);
        if (already)
        {
            await tx.CommitAsync(cancellationToken);
            return new CallbackWriteResult(CallbackWriteKind.Duplicate, topup.Status);
        }

        if (eventSeq < topup.HighestEventSeq)
        {
            _db.OperatorCallbacks.Add(CallbackRow(topupId, eventSeq, status, operatorTxnId, applied: false, utcNow));
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return new CallbackWriteResult(CallbackWriteKind.Ignored, topup.Status);
        }

        if (TopupStatusNames.IsTerminal(topup.Status))
        {
            if (topup.Status == TopupStatus.Expired)
            {
                _db.LateCallbacks.Add(new LateCallback
                {
                    TopupId = topupId,
                    EventSeq = eventSeq,
                    Status = status,
                    OperatorTxnId = operatorTxnId,
                    ReceivedAtUtc = utcNow,
                    Note = "Late callback after EXPIRED. The wallet was not debited again."
                });
            }

            _db.OperatorCallbacks.Add(CallbackRow(topupId, eventSeq, status, operatorTxnId, applied: false, utcNow));
            if (eventSeq > topup.HighestEventSeq)
            {
                topup.HighestEventSeq = eventSeq;
            }

            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return new CallbackWriteResult(
                topup.Status == TopupStatus.Expired ? CallbackWriteKind.Late : CallbackWriteKind.Ignored,
                topup.Status);
        }

        var houseId = await _db.Accounts.AsNoTracking()
            .Where(a => a.AccountType == AccountTypes.HouseClearing)
            .Select(a => a.Id)
            .SingleAsync(cancellationToken);
        var walletAccountId = await _db.Wallets.AsNoTracking()
            .Where(w => w.PartnerId == topup.PartnerId)
            .Select(w => w.AccountId)
            .SingleAsync(cancellationToken);

        if (status == "SUCCESS")
        {
            topup.Status = TopupStatus.Success;
            topup.OperatorTxnId = operatorTxnId;
            topup.CompletedAtUtc = utcNow;
            topup.HighestEventSeq = eventSeq;
            if (topup.DistributorShareFils > 0)
            {
                var parentId = await _db.Partners.AsNoTracking()
                    .Where(p => p.Id == topup.PartnerId)
                    .Select(p => p.ParentPartnerId)
                    .SingleAsync(cancellationToken);
                if (parentId is Guid parent)
                {
                    var parentAccount = await _db.Wallets.AsNoTracking()
                        .Where(w => w.PartnerId == parent)
                        .Select(w => w.AccountId)
                        .SingleAsync(cancellationToken);
                    var credited = await _db.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE wallets SET balance_fils = balance_fils + {topup.DistributorShareFils} WHERE partner_id = {parent}",
                        cancellationToken);
                    if (credited != 1)
                    {
                        throw new InvalidOperationException("Parent distributor wallet was not found.");
                    }

                    var (houseAmount, parentAmount) = LedgerPair.Split(topup.DistributorShareFils);
                    AddPair(
                        Guid.NewGuid(),
                        houseId,
                        houseAmount,
                        LedgerEntryTypes.DistributorShareContra,
                        parentAccount,
                        parentAmount,
                        LedgerEntryTypes.DistributorShare,
                        topup.Id,
                        topup.CorrelationId,
                        null,
                        utcNow);
                }
            }
        }
        else
        {
            topup.Status = TopupStatus.Failed;
            topup.OperatorTxnId = operatorTxnId;
            topup.CompletedAtUtc = utcNow;
            topup.HighestEventSeq = eventSeq;
            await RefundAsync(topup, walletAccountId, houseId, utcNow, cancellationToken);
        }

        _db.OperatorCallbacks.Add(CallbackRow(topupId, eventSeq, status, operatorTxnId, applied: true, utcNow));
        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new CallbackWriteResult(CallbackWriteKind.Applied, topup.Status);
    }

    public async Task<int> ExpirePendingAsync(DateTime utcNow, TimeSpan maxAge, int batchSize, CancellationToken cancellationToken)
    {
        var cutoff = utcNow.Subtract(maxAge);
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var ids = await QueryGuidsAsync(
            """
            SELECT id FROM topups
            WHERE status = 'PENDING' AND created_at_utc <= @cutoff
            ORDER BY created_at_utc
            LIMIT @batch
            FOR UPDATE SKIP LOCKED
            """,
            [P("@cutoff", cutoff), new MySqlParameter("@batch", batchSize)],
            cancellationToken);
        if (ids.Count == 0)
        {
            await tx.CommitAsync(cancellationToken);
            return 0;
        }

        var houseId = await _db.Accounts.AsNoTracking()
            .Where(a => a.AccountType == AccountTypes.HouseClearing)
            .Select(a => a.Id)
            .SingleAsync(cancellationToken);
        var expired = 0;
        foreach (var id in ids)
        {
            var topup = await _db.Topups.SingleAsync(t => t.Id == id, cancellationToken);
            if (topup.Status != TopupStatus.Pending)
            {
                continue;
            }

            var walletAccountId = await _db.Wallets.AsNoTracking()
                .Where(w => w.PartnerId == topup.PartnerId)
                .Select(w => w.AccountId)
                .SingleAsync(cancellationToken);
            await RefundAsync(topup, walletAccountId, houseId, utcNow, cancellationToken);
            topup.Status = TopupStatus.Expired;
            topup.CompletedAtUtc = utcNow;
            expired++;
        }

        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return expired;
    }

    public async Task<IReadOnlyList<DispatchedTopup>> ClaimOperatorDispatchAsync(DateTime utcNow, int batchSize, CancellationToken cancellationToken)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var ids = await QueryGuidsAsync(
            """
            SELECT id FROM topups
            WHERE status = 'PENDING' AND dispatched_at_utc IS NULL
            ORDER BY created_at_utc
            LIMIT @batch
            FOR UPDATE SKIP LOCKED
            """,
            [new MySqlParameter("@batch", batchSize)],
            cancellationToken);
        if (ids.Count == 0)
        {
            await tx.CommitAsync(cancellationToken);
            return Array.Empty<DispatchedTopup>();
        }

        var rows = await _db.Topups.Where(t => ids.Contains(t.Id)).ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            row.DispatchedAtUtc = utcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return rows.Select(r => new DispatchedTopup(r.Id, r.Operator)).ToList();
    }

    public async Task CreditWalletAsync(
        Guid partnerId,
        long amountFils,
        string reason,
        string? correlationId,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var updated = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE wallets SET balance_fils = balance_fils + {amountFils} WHERE partner_id = {partnerId}",
            cancellationToken);
        if (updated != 1)
        {
            throw new InvalidOperationException("Wallet was not found.");
        }

        var houseId = await _db.Accounts.AsNoTracking().Where(a => a.AccountType == AccountTypes.HouseClearing).Select(a => a.Id).SingleAsync(cancellationToken);
        var walletAccountId = await _db.Wallets.AsNoTracking().Where(w => w.PartnerId == partnerId).Select(w => w.AccountId).SingleAsync(cancellationToken);
        var (houseAmount, walletAmount) = LedgerPair.Split(amountFils);
        AddPair(Guid.NewGuid(), houseId, houseAmount, LedgerEntryTypes.ManualCreditContra, walletAccountId, walletAmount, LedgerEntryTypes.ManualCredit, null, correlationId, reason, utcNow);
        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<Guid> EnqueueSettlementAsync(DateOnly businessDay, DateTime utcNow, CancellationToken cancellationToken)
    {
        var existing = await _db.SettlementBatches.SingleOrDefaultAsync(b => b.BusinessDay == businessDay, cancellationToken);
        if (existing is null)
        {
            var created = new SettlementBatch
            {
                Id = Guid.NewGuid(),
                BusinessDay = businessDay,
                Status = SettlementStatuses.Queued,
                CreatedAtUtc = utcNow
            };
            _db.SettlementBatches.Add(created);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                return created.Id;
            }
            catch (DbUpdateException ex) when (IsDuplicate(ex))
            {
                _db.ChangeTracker.Clear();
                existing = await _db.SettlementBatches.SingleAsync(b => b.BusinessDay == businessDay, cancellationToken);
            }
        }

        if (existing.Status == SettlementStatuses.Failed)
        {
            existing.Status = SettlementStatuses.Queued;
            existing.Error = null;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return existing.Id;
    }

    public async Task<bool> TryRunNextSettlementAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        Guid batchId;
        DateOnly businessDay;
        await using (var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
        {
            var id = await QueryGuidAsync(
                """
                SELECT id FROM settlement_batches
                WHERE status = 'QUEUED'
                ORDER BY created_at_utc
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """,
                [],
                cancellationToken);
            if (id is null)
            {
                await tx.CommitAsync(cancellationToken);
                return false;
            }

            var batch = await _db.SettlementBatches.SingleAsync(b => b.Id == id, cancellationToken);
            batch.Status = SettlementStatuses.Running;
            batchId = batch.Id;
            businessDay = batch.BusinessDay;
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }

        try
        {
            await using var write = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await _db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM settlement_lines WHERE batch_id = {batchId}", cancellationToken);
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO settlement_lines
                    (batch_id, partner_id, sales_count, face_value_fils, retailer_commission_fils, distributor_share_fils, debit_fils, parent_distributor_income_fils)
                SELECT
                    {batchId},
                    u.partner_id,
                    COALESCE(s.sales_count, 0),
                    COALESCE(s.face_value_fils, 0),
                    COALESCE(s.retailer_commission_fils, 0),
                    COALESCE(s.distributor_share_fils, 0),
                    COALESCE(s.debit_fils, 0),
                    COALESCE(i.income_fils, 0)
                FROM (
                    SELECT partner_id FROM topups WHERE business_day = {businessDay} AND status = 'SUCCESS'
                    UNION
                    SELECT p.parent_partner_id
                    FROM topups t
                    INNER JOIN partners p ON p.id = t.partner_id
                    WHERE t.business_day = {businessDay} AND t.status = 'SUCCESS' AND p.parent_partner_id IS NOT NULL
                ) u
                LEFT JOIN (
                    SELECT partner_id,
                           COUNT(*) AS sales_count,
                           SUM(face_value_fils) AS face_value_fils,
                           SUM(retailer_commission_fils) AS retailer_commission_fils,
                           SUM(distributor_share_fils) AS distributor_share_fils,
                           SUM(debit_fils) AS debit_fils
                    FROM topups
                    WHERE business_day = {businessDay} AND status = 'SUCCESS'
                    GROUP BY partner_id
                ) s ON s.partner_id = u.partner_id
                LEFT JOIN (
                    SELECT p.parent_partner_id AS partner_id, SUM(t.distributor_share_fils) AS income_fils
                    FROM topups t
                    INNER JOIN partners p ON p.id = t.partner_id
                    WHERE t.business_day = {businessDay} AND t.status = 'SUCCESS' AND p.parent_partner_id IS NOT NULL
                    GROUP BY p.parent_partner_id
                ) i ON i.partner_id = u.partner_id
                """,
                cancellationToken);

            var batch = await _db.SettlementBatches.SingleAsync(b => b.Id == batchId, cancellationToken);
            batch.Status = SettlementStatuses.Ready;
            batch.CompletedAtUtc = utcNow;
            batch.Error = null;
            await _db.SaveChangesAsync(cancellationToken);
            await write.CommitAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _db.ChangeTracker.Clear();
            var batch = await _db.SettlementBatches.SingleAsync(b => b.Id == batchId, cancellationToken);
            batch.Status = SettlementStatuses.Failed;
            batch.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            await _db.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    private async Task RefundAsync(Topup topup, Guid walletAccountId, Guid houseId, DateTime utcNow, CancellationToken cancellationToken)
    {
        var refunded = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE wallets SET balance_fils = balance_fils + {topup.DebitFils} WHERE partner_id = {topup.PartnerId}",
            cancellationToken);
        if (refunded != 1)
        {
            throw new InvalidOperationException("Wallet refund did not update a row.");
        }

        var (houseAmount, walletAmount) = LedgerPair.Split(topup.DebitFils);
        AddPair(
            Guid.NewGuid(),
            houseId,
            houseAmount,
            LedgerEntryTypes.TopupRefundContra,
            walletAccountId,
            walletAmount,
            LedgerEntryTypes.TopupRefund,
            topup.Id,
            topup.CorrelationId,
            null,
            utcNow);
    }

    private void AddPair(
        Guid movementId,
        Guid debitAccount,
        long debitAmount,
        string debitType,
        Guid creditAccount,
        long creditAmount,
        string creditType,
        Guid? topupId,
        string? correlationId,
        string? reason,
        DateTime utcNow)
    {
        if (debitAmount + creditAmount != 0)
        {
            throw new InvalidOperationException("Ledger pair is not balanced.");
        }

        _db.LedgerEntries.Add(new LedgerEntry
        {
            TransactionId = movementId,
            AccountId = debitAccount,
            AmountFils = debitAmount,
            EntryType = debitType,
            TopupId = topupId,
            CorrelationId = correlationId,
            Reason = reason,
            CreatedAtUtc = utcNow
        });
        _db.LedgerEntries.Add(new LedgerEntry
        {
            TransactionId = movementId,
            AccountId = creditAccount,
            AmountFils = creditAmount,
            EntryType = creditType,
            TopupId = topupId,
            CorrelationId = correlationId,
            Reason = reason,
            CreatedAtUtc = utcNow
        });
    }

    private async Task<TopupWriteResult> FinishReplayAsync(Guid partnerId, Guid key, Guid topupId, DateTime utcNow, CancellationToken cancellationToken)
    {
        var topup = await _db.Topups.SingleAsync(t => t.Id == topupId, cancellationToken);
        var idem = await _db.IdempotencyRecords.SingleAsync(r => r.PartnerId == partnerId && r.IdempotencyKey == key, cancellationToken);
        if (idem.State != IdempotencyStates.Completed || idem.ResponseBody is null || idem.HttpStatus is null || idem.ContentType is null)
        {
            var rebuilt = ApiResultFor(topup);
            StampCompleted(idem, rebuilt.StatusCode, rebuilt.Body, rebuilt.ContentType, rebuilt.Location, utcNow);
            await _db.SaveChangesAsync(cancellationToken);
            return new TopupWriteResult(TopupWriteOutcome.StoredReplay, topup, rebuilt.StatusCode, rebuilt.Body, rebuilt.ContentType, rebuilt.Location);
        }

        return new TopupWriteResult(TopupWriteOutcome.StoredReplay, topup, idem.HttpStatus, idem.ResponseBody, idem.ContentType, idem.Location);
    }

    private async Task<TopupWriteResult> ReadStoredReplayAsync(Guid partnerId, Guid key, Topup topup, CancellationToken cancellationToken)
    {
        var idem = await _db.IdempotencyRecords.AsNoTracking().SingleAsync(r => r.PartnerId == partnerId && r.IdempotencyKey == key, cancellationToken);
        if (idem.ResponseBody is not null && idem.HttpStatus is not null && idem.ContentType is not null)
        {
            return new TopupWriteResult(TopupWriteOutcome.StoredReplay, topup, idem.HttpStatus, idem.ResponseBody, idem.ContentType, idem.Location);
        }

        var rebuilt = ApiResultFor(topup);
        return new TopupWriteResult(TopupWriteOutcome.StoredReplay, topup, rebuilt.StatusCode, rebuilt.Body, rebuilt.ContentType, rebuilt.Location);
    }

    private static Application.Api.ApiResult ApiResultFor(Topup topup)
    {
        var location = $"/api/v1/partner/topups/{topup.Id:D}";
        return Application.Api.ApiResult.Json(202, TopupBodies.From(topup), location);
    }

    private static void StampCompleted(IdempotencyRecord idem, int status, byte[] body, string contentType, string? location, DateTime utcNow)
    {
        idem.State = IdempotencyStates.Completed;
        idem.HttpStatus = status;
        idem.ResponseBody = body;
        idem.ContentType = contentType;
        idem.Location = location;
        idem.CompletedAtUtc = utcNow;
    }

    private static OperatorCallbackRecord CallbackRow(Guid topupId, int eventSeq, string status, string operatorTxnId, bool applied, DateTime utcNow) =>
        new()
        {
            TopupId = topupId,
            EventSeq = eventSeq,
            Status = status,
            OperatorTxnId = operatorTxnId,
            Applied = applied,
            ReceivedAtUtc = utcNow
        };

    private async Task LockAsync(string sql, IReadOnlyList<MySqlParameter> parameters, CancellationToken cancellationToken)
    {
        await using var reader = await ExecuteAsync(sql, parameters, cancellationToken);
    }

    private async Task<Guid?> QueryGuidAsync(string sql, IReadOnlyList<MySqlParameter> parameters, CancellationToken cancellationToken)
    {
        var ids = await QueryGuidsAsync(sql, parameters, cancellationToken);
        return ids.Count == 0 ? null : ids[0];
    }

    private async Task<List<Guid>> QueryGuidsAsync(string sql, IReadOnlyList<MySqlParameter> parameters, CancellationToken cancellationToken)
    {
        var list = new List<Guid>();
        await using var reader = await ExecuteAsync(sql, parameters, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(reader.GetGuid(0));
        }

        return list;
    }

    private async Task<MySqlDataReader> ExecuteAsync(string sql, IReadOnlyList<MySqlParameter> parameters, CancellationToken cancellationToken)
    {
        var connection = (MySqlConnection)_db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        var command = connection.CreateCommand();
        command.Transaction = (MySqlTransaction?)_db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        return await command.ExecuteReaderAsync(cancellationToken);
    }

    private static MySqlParameter P(string name, Guid value) => new(name, MySqlDbType.Guid) { Value = value };

    private static MySqlParameter P(string name, DateTime value) => new(name, MySqlDbType.DateTime) { Value = value };

    private async Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        const int attempts = 8;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _db.ChangeTracker.Clear();
                return await action(cancellationToken);
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < attempts)
            {
                _db.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(15 * attempt), cancellationToken);
            }
        }
    }

    private static bool IsDuplicate(DbUpdateException exception)
    {
        var message = exception.InnerException?.Message ?? exception.Message;
        return message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTransient(Exception exception)
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
