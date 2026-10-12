namespace TopupLedger.Domain.Msisdn;

public readonly record struct NormalizedMsisdn(string Value, string NationalPrefix, MobileOperator Operator);

public enum MobileOperator
{
    Etisalat = 1,
    Du = 2
}

public static class MobileOperatorParser
{
    public static bool TryParse(string? text, out MobileOperator op)
    {
        op = default;
        if (string.Equals(text, "ETISALAT", StringComparison.Ordinal))
        {
            op = MobileOperator.Etisalat;
            return true;
        }

        if (string.Equals(text, "DU", StringComparison.Ordinal))
        {
            op = MobileOperator.Du;
            return true;
        }

        return false;
    }

    public static string ToApi(MobileOperator op) => op switch
    {
        MobileOperator.Etisalat => "ETISALAT",
        MobileOperator.Du => "DU",
        _ => throw new ArgumentOutOfRangeException(nameof(op))
    };

    public static string ToRoute(MobileOperator op) => op switch
    {
        MobileOperator.Etisalat => "etisalat",
        MobileOperator.Du => "du",
        _ => throw new ArgumentOutOfRangeException(nameof(op))
    };

    public static bool TryParseRoute(string? route, out MobileOperator op)
    {
        op = default;
        if (string.Equals(route, "etisalat", StringComparison.OrdinalIgnoreCase))
        {
            op = MobileOperator.Etisalat;
            return true;
        }

        if (string.Equals(route, "du", StringComparison.OrdinalIgnoreCase))
        {
            op = MobileOperator.Du;
            return true;
        }

        return false;
    }
}

/// <summary>
/// Prefix-to-operator map required by this assignment. Real UAE portability is a
/// different lookup and is deliberately not invented here.
/// </summary>
public static class MsisdnNormalizer
{
    private static readonly Dictionary<string, MobileOperator> Prefixes = new(StringComparer.Ordinal)
    {
        ["050"] = MobileOperator.Etisalat,
        ["054"] = MobileOperator.Etisalat,
        ["056"] = MobileOperator.Etisalat,
        ["052"] = MobileOperator.Du,
        ["055"] = MobileOperator.Du,
        ["058"] = MobileOperator.Du
    };

    public static bool TryNormalize(string? input, out NormalizedMsisdn msisdn, out string? error)
    {
        msisdn = default;
        error = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "MSISDN is required.";
            return false;
        }

        var compact = new string(input.Where(c => c is not (' ' or '-')).ToArray());
        if (compact.StartsWith("+", StringComparison.Ordinal))
        {
            compact = compact[1..];
        }

        if (LooksLikeLandline(compact))
        {
            error = "Landline numbers are not accepted.";
            return false;
        }

        string international;
        if (compact.StartsWith("00971", StringComparison.Ordinal))
        {
            international = compact[2..];
        }
        else if (compact.StartsWith("971", StringComparison.Ordinal))
        {
            international = compact;
        }
        else if (compact.StartsWith('0') && compact.Length == 10)
        {
            international = "971" + compact[1..];
        }
        else if (compact.StartsWith('5') && compact.Length == 9)
        {
            international = "971" + compact;
        }
        else
        {
            error = "MSISDN must be a UAE mobile number.";
            return false;
        }

        if (international.Length != 12 || !international.All(char.IsDigit) || !international.StartsWith("9715", StringComparison.Ordinal))
        {
            error = "MSISDN must be a UAE mobile number.";
            return false;
        }

        var prefix = "0" + international.Substring(3, 2);
        if (!Prefixes.TryGetValue(prefix, out var op))
        {
            error = "MSISDN prefix is not a supported mobile operator.";
            return false;
        }

        msisdn = new NormalizedMsisdn(international, prefix, op);
        return true;
    }

    private static bool LooksLikeLandline(string compact)
    {
        var national = compact;
        if (national.StartsWith("00971", StringComparison.Ordinal))
        {
            national = "0" + national[5..];
        }
        else if (national.StartsWith("971", StringComparison.Ordinal))
        {
            national = "0" + national[3..];
        }

        return national.Length >= 2 &&
               national[0] == '0' &&
               national[1] is '2' or '3' or '4' or '6' or '7' or '9';
    }
}
