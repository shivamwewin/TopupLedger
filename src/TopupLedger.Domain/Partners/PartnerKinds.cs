namespace TopupLedger.Domain.Partners;

public enum PartnerTier
{
    Retailer = 1,
    Distributor = 2
}

public enum PartnerStatus
{
    Active = 1,
    Suspended = 2
}

public static class PartnerLabels
{
    public static string Tier(PartnerTier tier) => tier switch
    {
        PartnerTier.Retailer => "RETAILER",
        PartnerTier.Distributor => "DISTRIBUTOR",
        _ => throw new ArgumentOutOfRangeException(nameof(tier))
    };

    public static string Status(PartnerStatus status) => status switch
    {
        PartnerStatus.Active => "ACTIVE",
        PartnerStatus.Suspended => "SUSPENDED",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public static bool TryParseTier(string? text, out PartnerTier tier)
    {
        tier = default;
        if (string.Equals(text, "RETAILER", StringComparison.OrdinalIgnoreCase))
        {
            tier = PartnerTier.Retailer;
            return true;
        }

        if (string.Equals(text, "DISTRIBUTOR", StringComparison.OrdinalIgnoreCase))
        {
            tier = PartnerTier.Distributor;
            return true;
        }

        return false;
    }
}
