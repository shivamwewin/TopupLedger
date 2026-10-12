using TopupLedger.Domain.Money;

namespace TopupLedger.Application;

/// <summary>
/// The assigned variant is configuration, not a branch inside the money code.
/// V0 floor + Dubai midnight, V1 half-up + 22:00, V2 half-even + 23:30.
/// </summary>
public sealed record LedgerRules(string VariantCode, RoundingMode Rounding, TimeOnly BusinessDayCutoff)
{
    public static LedgerRules FromVariant(string? variantCode)
    {
        var code = string.IsNullOrWhiteSpace(variantCode) ? "V1" : variantCode.Trim().ToUpperInvariant();
        return code switch
        {
            "V0" => new LedgerRules("V0", RoundingMode.Floor, TimeOnly.MinValue),
            "V1" => new LedgerRules("V1", RoundingMode.HalfUp, new TimeOnly(22, 0, 0)),
            "V2" => new LedgerRules("V2", RoundingMode.ToEven, new TimeOnly(23, 30, 0)),
            _ => throw new InvalidOperationException(
                $"LedgerRules:Variant '{variantCode}' is not V0, V1, or V2. Set the variant Jstack assigned.")
        };
    }
}

public sealed record RateLimitRules(int PermitsPerSecond, int Burst);

public sealed record TopupTiming(TimeSpan ExpiryAfter, TimeSpan IdempotencyLease, TimeSpan IdempotencyTtl);
