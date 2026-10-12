using System.Globalization;

namespace TopupLedger.Domain.Money;

/// <summary>
/// AED amounts are stored as integer fils. 1 AED = 100 fils.
/// </summary>
public static class Fils
{
    public const long PerAed = 100;
    public const long MinFaceFils = 1;
    public const long MaxFaceFils = 200_000;

    public static long Round(decimal exactFils, RoundingMode mode)
    {
        if (exactFils < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(exactFils), "Commission rounding is defined for non-negative amounts.");
        }

        return mode switch
        {
            RoundingMode.Floor => (long)decimal.Floor(exactFils),
            RoundingMode.HalfUp => (long)decimal.Round(exactFils, 0, MidpointRounding.AwayFromZero),
            RoundingMode.ToEven => (long)decimal.Round(exactFils, 0, MidpointRounding.ToEven),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown rounding mode.")
        };
    }

    public static bool TryParseAed(string? text, out long fils, out string? error)
    {
        fils = 0;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Amount is required.";
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.Contains(',', StringComparison.Ordinal) ||
            trimmed.Contains('e', StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith('+') ||
            trimmed.StartsWith('-'))
        {
            error = "Amount must be a plain decimal between 0.01 and 2000.00.";
            return false;
        }

        var dot = trimmed.IndexOf('.');
        if (dot >= 0 && trimmed.Length - dot - 1 > 2)
        {
            error = "Amount must not have more than 2 decimal places.";
            return false;
        }

        if (!decimal.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var aed))
        {
            error = "Amount is not a valid decimal.";
            return false;
        }

        if (aed < 0.01m || aed > 2000.00m)
        {
            error = "Amount must be between 0.01 and 2000.00 AED.";
            return false;
        }

        var scaled = aed * PerAed;
        if (scaled != decimal.Truncate(scaled))
        {
            error = "Amount must not have more than 2 decimal places.";
            return false;
        }

        fils = (long)scaled;
        return true;
    }

    public static string FormatAed(long fils)
    {
        var sign = fils < 0 ? "-" : string.Empty;
        var abs = Math.Abs(fils);
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{abs / PerAed}.{abs % PerAed:00}");
    }
}
