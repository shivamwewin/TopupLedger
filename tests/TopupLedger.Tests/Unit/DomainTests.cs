using System.Text;
using TopupLedger.Application.Security;
using TopupLedger.Domain.Money;
using TopupLedger.Domain.Msisdn;
using TopupLedger.Domain.Time;
using TopupLedger.Infrastructure.Reconciliation;

namespace TopupLedger.Tests.Unit;

public class CommissionVectorTests
{
    public static TheoryData<string, decimal, string, long, long, string> Vectors { get; } = new()
    {
        { "V0", 13.53m, "2026-09-30T19:59:59Z", 27, 10, "2026-09-30" },
        { "V0", 125.05m, "2026-09-30T20:00:00Z", 312, 75, "2026-10-01" },
        { "V0", 125.05m, "2026-09-30T19:59:59Z", 293, 94, "2026-09-30" },
        { "V0", 49.99m, "2026-10-01T18:45:00Z", 99, 38, "2026-10-01" },
        { "V0", 200.00m, "2026-10-01T19:40:00Z", 520, 160, "2026-10-01" },
        { "V0", 109.80m, "2026-10-11T18:15:00Z", 274, 66, "2026-10-11" },
        { "V1", 13.53m, "2026-09-30T19:59:59Z", 27, 10, "2026-10-01" },
        { "V1", 125.05m, "2026-09-30T20:00:00Z", 313, 75, "2026-10-01" },
        { "V1", 125.05m, "2026-09-30T19:59:59Z", 294, 94, "2026-10-01" },
        { "V1", 49.99m, "2026-10-01T18:45:00Z", 100, 37, "2026-10-02" },
        { "V1", 200.00m, "2026-10-01T19:40:00Z", 520, 160, "2026-10-02" },
        { "V1", 109.80m, "2026-10-11T18:15:00Z", 275, 65, "2026-10-12" },
        { "V2", 13.53m, "2026-09-30T19:59:59Z", 27, 10, "2026-10-01" },
        { "V2", 125.05m, "2026-09-30T20:00:00Z", 313, 75, "2026-10-01" },
        { "V2", 125.05m, "2026-09-30T19:59:59Z", 294, 94, "2026-10-01" },
        { "V2", 49.99m, "2026-10-01T18:45:00Z", 100, 37, "2026-10-01" },
        { "V2", 200.00m, "2026-10-01T19:40:00Z", 520, 160, "2026-10-02" },
        { "V2", 109.80m, "2026-10-11T18:15:00Z", 274, 66, "2026-10-11" }
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Mandatory_vectors(string variant, decimal aed, string utc, long retailer, long share, string businessDay)
    {
        var rules = Application.LedgerRules.FromVariant(variant);
        Assert.True(Fils.TryParseAed(aed.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), out var fils, out _));
        var instant = DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);
        var quote = CommissionCalculator.Quote(fils, instant, rules.Rounding);
        var day = BusinessDayCalculator.ForInstant(instant, rules.BusinessDayCutoff);
        Assert.Equal(retailer, quote.RetailerCommissionFils);
        Assert.Equal(share, quote.DistributorShareFils);
        Assert.Equal(businessDay, day.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public void Half_up_and_bankers_differ_on_exact_half()
    {
        Assert.Equal(275, Fils.Round(274.5m, RoundingMode.HalfUp));
        Assert.Equal(274, Fils.Round(274.5m, RoundingMode.ToEven));
        Assert.Equal(274, Fils.Round(274.5m, RoundingMode.Floor));
    }

    [Fact]
    public void Slab_boundary_uses_the_new_retailer_rate_at_dubai_midnight()
    {
        var before = new DateTimeOffset(2026, 9, 30, 19, 59, 59, TimeSpan.Zero);
        var at = new DateTimeOffset(2026, 9, 30, 20, 0, 0, TimeSpan.Zero);
        Assert.Equal(2.35m, CommissionCalculator.ResolveRates(12_505, before).RetailerRate);
        Assert.Equal(2.50m, CommissionCalculator.ResolveRates(12_505, at).RetailerRate);
    }
}

public class MsisdnTests
{
    [Theory]
    [InlineData("0501234567", "971501234567", "ETISALAT")]
    [InlineData("501234567", "971501234567", "ETISALAT")]
    [InlineData("+971 50 123 4567", "971501234567", "ETISALAT")]
    [InlineData("00971-55-123-4567", "971551234567", "DU")]
    [InlineData("0559123456", "971559123456", "DU")]
    public void Normalises_mobile_numbers(string input, string expected, string op)
    {
        Assert.True(MsisdnNormalizer.TryNormalize(input, out var msisdn, out _));
        Assert.Equal(expected, msisdn.Value);
        Assert.Equal(op, MobileOperatorParser.ToApi(msisdn.Operator));
    }

    [Theory]
    [InlineData("04 123 4567")]
    [InlineData("+97141234567")]
    [InlineData("021234567")]
    public void Rejects_landlines(string input)
    {
        Assert.False(MsisdnNormalizer.TryNormalize(input, out _, out var error));
        Assert.Contains("Landline", error);
    }
}

public class HmacVectorTests
{
    [Fact]
    public void Verification_vector_matches_the_assignment_body()
    {
        var body = Encoding.UTF8.GetBytes("{\"msisdn\":\"0559123456\",\"amount\":\"25.00\",\"operator\":\"ETISALAT\",\"clientRef\":\"RTL-000981\"}");
        var hash = CanonicalSigner.HashBody(body);
        Assert.Equal("bd1ab69d25906f9c0eccdbb3249655e08971a669e544bfdfe37fd59d55f75157", hash);
        var canonical = CanonicalSigner.Canonical(
            "POST",
            "/api/v1/partner/topups",
            "1791792000",
            "7f3c2a91-5d4e-4b8a-9c1f-0e6d2b7a4c55",
            hash);
        var signature = Convert.ToHexString(CanonicalSigner.Sign("sk_test_E3JNS3_UAE_2026_k7Rm", canonical)).ToLowerInvariant();
        Assert.Equal("45c89cfe8595d983633e833de7dc3827e7f018195fa64dbc78b2a6f40c508ea2", signature);
        Assert.True(CanonicalSigner.FixedTimeEqualsHex(
            CanonicalSigner.Sign("sk_test_E3JNS3_UAE_2026_k7Rm", canonical),
            signature));
        Assert.False(CanonicalSigner.FixedTimeEqualsHex(
            CanonicalSigner.Sign("sk_test_E3JNS3_UAE_2026_k7Rm", canonical),
            "00"));
    }
}

public class CsvSplitterTests
{
    [Fact]
    public void Handles_quotes_thousand_separators_and_a_repeated_header()
    {
        var header = ReconciliationImporter.SplitCsv("operator_txn_id,msisdn,amount_aed,status,event_time_dubai");
        var quoted = ReconciliationImporter.SplitCsv("ETS-1,971561122334,\"1,000.00\",FAILED,2026-10-11 22:16:40");
        Assert.Equal("operator_txn_id", header[0]);
        Assert.Equal("1,000.00", quoted[2]);
        Assert.Equal(5, quoted.Count);
    }
}

public class MoneySourceTests
{
    [Fact]
    public void Domain_source_does_not_use_binary_floating_point()
    {
        var root = FindUp("src");
        var files = Directory.GetFiles(Path.Combine(root, "src", "TopupLedger.Domain"), "*.cs", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotMatch(@"\bdouble\b", text);
            Assert.DoesNotMatch(@"\bfloat\b", text);
        }
    }

    private static string FindUp(string marker)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, marker)))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
