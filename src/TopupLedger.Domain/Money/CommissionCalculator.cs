namespace TopupLedger.Domain.Money;

public readonly record struct CommissionQuote(
    long FaceValueFils,
    decimal RetailerRatePercent,
    decimal DistributorRatePercent,
    long RetailerCommissionFils,
    long DistributorShareFils,
    long PartnerDebitFils);

/// <summary>
/// Commission is a percentage of face value. Each percentage is rounded to whole fils
/// on its own, then the distributor share is the difference.
/// Partner debit = face − retailer commission. The distributor is not paid until SUCCESS.
/// </summary>
public static class CommissionCalculator
{
    public static readonly TimeSpan DubaiOffset = TimeSpan.FromHours(4);

    private static readonly DateTimeOffset MidSlabRateChange =
        new(2026, 10, 1, 0, 0, 0, DubaiOffset);

    public static CommissionQuote Quote(long faceValueFils, DateTimeOffset utcInstant, RoundingMode rounding)
    {
        if (faceValueFils is < Fils.MinFaceFils or > Fils.MaxFaceFils)
        {
            throw new ArgumentOutOfRangeException(nameof(faceValueFils), "Face value is outside 0.01–2000.00 AED.");
        }

        var (retailerRate, distributorRate) = ResolveRates(faceValueFils, utcInstant);
        var retailer = Fils.Round(faceValueFils * retailerRate / 100m, rounding);
        var distributorRounded = Fils.Round(faceValueFils * distributorRate / 100m, rounding);
        var share = distributorRounded - retailer;
        var debit = faceValueFils - retailer;
        if (debit <= 0)
        {
            throw new InvalidOperationException("Partner debit must be a positive number of fils.");
        }

        return new CommissionQuote(faceValueFils, retailerRate, distributorRate, retailer, share, debit);
    }

    public static (decimal RetailerRate, decimal DistributorRate) ResolveRates(long faceValueFils, DateTimeOffset utcInstant)
    {
        var dubai = utcInstant.ToOffset(DubaiOffset);
        if (faceValueFils <= 4_999)
        {
            return (2.00m, 2.75m);
        }

        if (faceValueFils <= 19_999)
        {
            // Inclusive from, exclusive to. 2026-09-30 23:59:59 Dubai is still 2.35%.
            // 2026-10-01 00:00:00 Dubai is 2.50%.
            var retailer = dubai >= MidSlabRateChange ? 2.50m : 2.35m;
            return (retailer, 3.10m);
        }

        return (2.60m, 3.40m);
    }
}
