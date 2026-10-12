using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TopupLedger.Application;
using TopupLedger.Application.Persistence;
using TopupLedger.Application.Security;
using TopupLedger.Application.Topups;
using TopupLedger.Infrastructure.Persistence;
using TopupLedger.Infrastructure.Reconciliation;
using TopupLedger.Infrastructure.Security;

namespace TopupLedger.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("TopupLedger");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:TopupLedger is required.");
        }
        if (!connectionString.Contains("GuidFormat=", StringComparison.OrdinalIgnoreCase))
        {
            connectionString += ";GuidFormat=Char36";
        }

        var rules = LedgerRules.FromVariant(configuration["LedgerRules:Variant"]);
        services.AddSingleton(rules);
        services.AddSingleton(new RateLimitRules(
            configuration.GetValue("RateLimit:PermitsPerSecond", 20),
            configuration.GetValue("RateLimit:Burst", 40)));
        services.AddSingleton(new TopupTiming(
            TimeSpan.FromSeconds(configuration.GetValue("Topup:ExpirySeconds", 120)),
            TimeSpan.FromSeconds(configuration.GetValue("Topup:IdempotencyLeaseSeconds", 30)),
            TimeSpan.FromHours(configuration.GetValue("Topup:IdempotencyTtlHours", 24))));
        services.AddSingleton(new OperatorTokens(
            configuration["Operators:EtisalatToken"] ?? string.Empty,
            configuration["Operators:DuToken"] ?? string.Empty));

        var keyBase64 = configuration["DataProtection:KeyBase64"];
        if (string.IsNullOrWhiteSpace(keyBase64))
        {
            throw new InvalidOperationException("DataProtection:KeyBase64 is required and must not be committed for production.");
        }

        services.AddSingleton<ISecretProtector>(_ => new AesGcmSecretProtector(Convert.FromBase64String(keyBase64)));
        services.AddDbContext<LedgerDbContext>(options =>
            options.UseMySql(connectionString, ServerVersion.Parse("8.0.36-mysql")));
        services.AddScoped<ILedgerDbContext>(sp => sp.GetRequiredService<LedgerDbContext>());
        services.AddScoped<IFinancialWriteStore, FinancialWriteStore>();
        services.AddScoped<IPartnerAuthenticator, PartnerAuthenticator>();
        services.AddScoped<IPartnerRateLimiter, MySqlPartnerRateLimiter>();
        services.AddScoped<IReconciliationImporter, ReconciliationImporter>();
        return services;
    }
}
