using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using TopupLedger.Domain.Entities;

namespace TopupLedger.Application.Persistence;

public interface ILedgerDbContext
{
    DbSet<Partner> Partners { get; }
    DbSet<Account> Accounts { get; }
    DbSet<Wallet> Wallets { get; }
    DbSet<LedgerEntry> LedgerEntries { get; }
    DbSet<Topup> Topups { get; }
    DbSet<IdempotencyRecord> IdempotencyRecords { get; }
    DbSet<OperatorCallbackRecord> OperatorCallbacks { get; }
    DbSet<LateCallback> LateCallbacks { get; }
    DbSet<RateBucket> RateBuckets { get; }
    DbSet<SettlementBatch> SettlementBatches { get; }
    DbSet<SettlementLine> SettlementLines { get; }
    DbSet<ReconciliationRun> ReconciliationRuns { get; }
    DbSet<ReconciliationLine> ReconciliationLines { get; }
    DbSet<ReconciliationFinding> ReconciliationFindings { get; }
    DbSet<PortalUser> PortalUsers { get; }

    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
