namespace TopupLedger.Domain.Money;

/// <summary>
/// How a positive exact fil amount is reduced to a whole number of fils.
/// Binary floating point is never used.
/// </summary>
public enum RoundingMode
{
    /// <summary>Truncate toward zero. Variant V0.</summary>
    Floor = 0,

    /// <summary>Midpoint 0.5 rounds away from zero. Variant V1.</summary>
    HalfUp = 1,

    /// <summary>Midpoint 0.5 rounds to the even integer. Variant V2.</summary>
    ToEven = 2
}
