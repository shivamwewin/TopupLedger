using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TopupLedger.Application.Persistence;
using TopupLedger.Domain.Entities;
using TopupLedger.Domain.Msisdn;
using TopupLedger.Domain.Partners;
using TopupLedger.Domain.Topups;

namespace TopupLedger.Infrastructure.Persistence;

public sealed class LedgerDbContext : DbContext, ILedgerDbContext
{
    public LedgerDbContext(DbContextOptions<LedgerDbContext> options) : base(options)
    {
    }

    public DbSet<Partner> Partners => Set<Partner>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<Topup> Topups => Set<Topup>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<OperatorCallbackRecord> OperatorCallbacks => Set<OperatorCallbackRecord>();
    public DbSet<LateCallback> LateCallbacks => Set<LateCallback>();
    public DbSet<RateBucket> RateBuckets => Set<RateBucket>();
    public DbSet<SettlementBatch> SettlementBatches => Set<SettlementBatch>();
    public DbSet<SettlementLine> SettlementLines => Set<SettlementLine>();
    public DbSet<ReconciliationRun> ReconciliationRuns => Set<ReconciliationRun>();
    public DbSet<ReconciliationLine> ReconciliationLines => Set<ReconciliationLine>();
    public DbSet<ReconciliationFinding> ReconciliationFindings => Set<ReconciliationFinding>();
    public DbSet<PortalUser> PortalUsers => Set<PortalUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var utc = new ValueConverter<DateTime, DateTime>(
            v => v,
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcNull = new ValueConverter<DateTime?, DateTime?>(
            v => v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

        modelBuilder.Entity<Partner>(e =>
        {
            e.ToTable("partners");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.ApiKey).HasMaxLength(80).IsRequired();
            e.HasIndex(x => x.ApiKey).IsUnique();
            e.Property(x => x.SecretProtected).HasColumnType("varbinary(512)").IsRequired();
            e.Property(x => x.Tier).HasConversion(v => PartnerLabels.Tier(v), v => ParseTier(v)).HasMaxLength(20);
            e.Property(x => x.Status).HasConversion(v => PartnerLabels.Status(v), v => ParseStatus(v)).HasMaxLength(20);
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentPartnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Wallet).WithOne(x => x.Partner).HasForeignKey<Wallet>(x => x.PartnerId);
        });

        modelBuilder.Entity<Account>(e =>
        {
            e.ToTable("accounts");
            e.HasKey(x => x.Id);
            e.Property(x => x.AccountType).HasMaxLength(32).IsRequired();
            e.HasIndex(x => x.PartnerId).IsUnique();
            e.HasOne<Partner>().WithMany().HasForeignKey(x => x.PartnerId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Wallet>(e =>
        {
            e.ToTable("wallets");
            e.HasKey(x => x.PartnerId);
            e.HasIndex(x => x.AccountId).IsUnique();
            e.Property(x => x.BalanceFils).IsRequired();
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_wallets_balance_non_negative", "balance_fils >= 0"));
        });

        modelBuilder.Entity<LedgerEntry>(e =>
        {
            e.ToTable("ledger_entries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.EntryType).HasMaxLength(40).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.HasIndex(x => x.TransactionId);
            e.HasIndex(x => new { x.AccountId, x.Id });
            e.HasIndex(x => x.TopupId);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Topup>(e =>
        {
            e.ToTable("topups");
            e.HasKey(x => x.Id);
            e.Property(x => x.ClientRef).HasMaxLength(64).IsRequired();
            e.Property(x => x.Msisdn).HasMaxLength(15).IsRequired();
            e.Property(x => x.Operator).HasConversion(v => MobileOperatorParser.ToApi(v), v => ParseOperator(v)).HasMaxLength(20);
            e.Property(x => x.Status).HasConversion(v => TopupStatusNames.ToApi(v), v => ParseTopupStatus(v)).HasMaxLength(20);
            e.Property(x => x.OperatorTxnId).HasMaxLength(64);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.HasIndex(x => new { x.PartnerId, x.ClientRef }).IsUnique().HasDatabaseName("uk_topups_partner_client");
            e.HasIndex(x => new { x.PartnerId, x.IdempotencyKey }).IsUnique().HasDatabaseName("uk_topups_partner_idem");
            e.HasIndex(x => new { x.PartnerId, x.CreatedAtUtc, x.Id });
            e.HasIndex(x => new { x.Msisdn, x.CreatedAtUtc, x.Id });
            e.HasIndex(x => new { x.Status, x.CreatedAtUtc, x.Id });
            e.HasIndex(x => new { x.BusinessDay, x.PartnerId });
            e.HasIndex(x => x.OperatorTxnId);
            e.HasIndex(x => new { x.Status, x.DispatchedAtUtc, x.CreatedAtUtc });
            e.HasOne<Partner>().WithMany().HasForeignKey(x => x.PartnerId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("idempotency_records");
            e.HasKey(x => new { x.PartnerId, x.IdempotencyKey });
            e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.State).HasMaxLength(20).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(80);
            e.Property(x => x.Location).HasMaxLength(200);
            e.Property(x => x.ResponseBody).HasColumnType("mediumblob");
            e.HasOne<Partner>().WithMany().HasForeignKey(x => x.PartnerId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OperatorCallbackRecord>(e =>
        {
            e.ToTable("operator_callbacks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.OperatorTxnId).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.TopupId, x.EventSeq }).IsUnique();
            e.HasOne<Topup>().WithMany().HasForeignKey(x => x.TopupId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LateCallback>(e =>
        {
            e.ToTable("late_callbacks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.OperatorTxnId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Note).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.TopupId, x.EventSeq });
            e.HasOne<Topup>().WithMany().HasForeignKey(x => x.TopupId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RateBucket>(e =>
        {
            e.ToTable("rate_buckets");
            e.HasKey(x => x.PartnerId);
        });

        modelBuilder.Entity<SettlementBatch>(e =>
        {
            e.ToTable("settlement_batches");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.Error).HasMaxLength(500);
            e.HasIndex(x => x.BusinessDay).IsUnique();
        });

        modelBuilder.Entity<SettlementLine>(e =>
        {
            e.ToTable("settlement_lines");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.BatchId, x.PartnerId }).IsUnique();
            e.HasOne<SettlementBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Partner>().WithMany().HasForeignKey(x => x.PartnerId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ReconciliationRun>(e =>
        {
            e.ToTable("reconciliation_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.FileSha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.HasIndex(x => x.FileSha256).IsUnique();
        });

        modelBuilder.Entity<ReconciliationLine>(e =>
        {
            e.ToTable("reconciliation_lines");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.OperatorTxnId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Msisdn).HasMaxLength(20).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.HasIndex(x => new { x.RunId, x.OperatorTxnId }).IsUnique();
            e.HasOne<ReconciliationRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ReconciliationFinding>(e =>
        {
            e.ToTable("reconciliation_findings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Category).HasMaxLength(32).IsRequired();
            e.Property(x => x.OperatorTxnId).HasMaxLength(64);
            e.Property(x => x.Detail).HasMaxLength(500).IsRequired();
            e.HasIndex(x => new { x.RunId, x.Category });
            e.HasOne<ReconciliationRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PortalUser>(e =>
        {
            e.ToTable("portal_users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Username).HasMaxLength(80).IsRequired();
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.PasswordHash).HasColumnType("varbinary(64)").IsRequired();
            e.Property(x => x.PasswordSalt).HasColumnType("varbinary(32)").IsRequired();
            e.Property(x => x.Role).HasMaxLength(20).IsRequired();
        });

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnake(property.Name));
                if (property.ClrType == typeof(DateTime))
                {
                    property.SetValueConverter(utc);
                    property.SetColumnType("datetime(6)");
                }
                else if (property.ClrType == typeof(DateTime?))
                {
                    property.SetValueConverter(utcNull);
                    property.SetColumnType("datetime(6)");
                }
                else if (property.ClrType == typeof(Guid) || property.ClrType == typeof(Guid?))
                {
                    property.SetColumnType("char(36)");
                }
            }
        }
    }

    private static string ToSnake(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    private static PartnerTier ParseTier(string value) =>
        PartnerLabels.TryParseTier(value, out var tier) ? tier : throw new InvalidOperationException(value);

    private static PartnerStatus ParseStatus(string value) => value switch
    {
        "ACTIVE" => PartnerStatus.Active,
        "SUSPENDED" => PartnerStatus.Suspended,
        _ => throw new InvalidOperationException(value)
    };

    private static MobileOperator ParseOperator(string value) =>
        MobileOperatorParser.TryParse(value, out var op) ? op : throw new InvalidOperationException(value);

    private static TopupStatus ParseTopupStatus(string value) =>
        TopupStatusNames.TryParse(value, out var status) ? status : throw new InvalidOperationException(value);
}
