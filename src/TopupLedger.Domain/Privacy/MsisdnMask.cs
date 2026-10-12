namespace TopupLedger.Domain.Privacy;

public static class MsisdnMask
{
    public static string Apply(string? normalized)
    {
        if (string.IsNullOrEmpty(normalized) || normalized.Length < 6)
        {
            return "****";
        }

        return string.Concat(normalized.AsSpan(0, 4), new string('*', normalized.Length - 6), normalized.AsSpan(normalized.Length - 2));
    }
}
