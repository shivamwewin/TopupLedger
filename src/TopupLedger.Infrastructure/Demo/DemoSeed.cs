using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TopupLedger.Application.Security;
using TopupLedger.Domain.Entities;
using TopupLedger.Domain.Ledger;
using TopupLedger.Domain.Msisdn;
using TopupLedger.Domain.Partners;
using TopupLedger.Infrastructure.Persistence;
using TopupLedger.Infrastructure.Security;

namespace TopupLedger.Infrastructure.Demo;

/// <summary>
/// Local demo data. Partner secrets are inserted as AES-GCM ciphertext.
/// The plaintext retailer secret is the assignment's published HMAC vector, not a production key.
/// </summary>
public static class DemoSeed
{
    public static readonly Guid HouseAccountId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid DistributorId = Guid.Parse("20000000-0000-0000-0000-000000000002");
    public static readonly Guid DistributorAccountId = Guid.Parse("20000000-0000-0000-0000-000000000012");
    public static readonly Guid RetailerId = Guid.Parse("30000000-0000-0000-0000-000000000003");
    public static readonly Guid RetailerAccountId = Guid.Parse("30000000-0000-0000-0000-000000000013");
    public static readonly Guid SuspendedId = Guid.Parse("40000000-0000-0000-0000-000000000004");
    public static readonly Guid SuspendedAccountId = Guid.Parse("40000000-0000-0000-0000-000000000014");
    public static readonly Guid OpeningMovementId = Guid.Parse("50000000-0000-0000-0000-000000000005");

    public const string RetailerApiKey = "ak_test_retail_marina";
    public const string RetailerSecret = "sk_test_E3JNS3_UAE_2026_k7Rm";
    public const string DistributorApiKey = "ak_test_distributor";
    public const string DistributorSecret = "sk_test_distributor_demo";
    public const string SuspendedApiKey = "ak_test_suspended";
    public const string SuspendedSecret = "sk_test_suspended_demo";
    public const long RetailerOpeningFils = 5_000_000;

    public static async Task EnsureAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DemoSeed");
        if (await db.Accounts.AnyAsync(a => a.Id == HouseAccountId, cancellationToken))
        {
            await EnsureUsersAsync(db, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var now = DateTime.UtcNow;
        db.Accounts.Add(new Account { Id = HouseAccountId, AccountType = AccountTypes.HouseClearing });
        AddPartner(db, protector, DistributorId, DistributorAccountId, "Dubai Distribution", PartnerTier.Distributor, PartnerStatus.Active, DistributorApiKey, DistributorSecret, null, 0, now);
        AddPartner(db, protector, RetailerId, RetailerAccountId, "Marina Retail", PartnerTier.Retailer, PartnerStatus.Active, RetailerApiKey, RetailerSecret, DistributorId, RetailerOpeningFils, now);
        AddPartner(db, protector, SuspendedId, SuspendedAccountId, "Suspended Kiosk", PartnerTier.Retailer, PartnerStatus.Suspended, SuspendedApiKey, SuspendedSecret, DistributorId, 0, now);
        var (houseAmount, walletAmount) = LedgerPair.Split(RetailerOpeningFils);
        db.LedgerEntries.Add(new LedgerEntry
        {
            TransactionId = OpeningMovementId,
            AccountId = HouseAccountId,
            AmountFils = houseAmount,
            EntryType = LedgerEntryTypes.OpeningBalanceContra,
            Reason = "Opening float",
            CreatedAtUtc = now
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            TransactionId = OpeningMovementId,
            AccountId = RetailerAccountId,
            AmountFils = walletAmount,
            EntryType = LedgerEntryTypes.OpeningBalance,
            Reason = "Opening float",
            CreatedAtUtc = now
        });
        await EnsureUsersAsync(db, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Demo partners and portal users are ready.");
    }

    private static void AddPartner(
        LedgerDbContext db,
        ISecretProtector protector,
        Guid partnerId,
        Guid accountId,
        string name,
        PartnerTier tier,
        PartnerStatus status,
        string apiKey,
        string secret,
        Guid? parentId,
        long openingFils,
        DateTime now)
    {
        db.Partners.Add(new Partner
        {
            Id = partnerId,
            Name = name,
            Tier = tier,
            Status = status,
            ApiKey = apiKey,
            SecretProtected = protector.Protect(secret),
            ParentPartnerId = parentId,
            CreatedAtUtc = now
        });
        db.Accounts.Add(new Account { Id = accountId, PartnerId = partnerId, AccountType = AccountTypes.PartnerWallet });
        db.Wallets.Add(new Wallet { PartnerId = partnerId, AccountId = accountId, BalanceFils = openingFils });
    }

    private static async Task EnsureUsersAsync(LedgerDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.PortalUsers.AnyAsync(cancellationToken))
        {
            db.PortalUsers.Add(User("admin", "Admin123!", "ADMIN"));
            db.PortalUsers.Add(User("viewer", "Viewer123!", "VIEWER"));
        }
    }

    private static PortalUser User(string username, string password, string role)
    {
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        return new PortalUser
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = PortalPassword.Hash(password, salt),
            PasswordSalt = salt,
            Role = role,
            CreatedAtUtc = DateTime.UtcNow
        };
    }
}
