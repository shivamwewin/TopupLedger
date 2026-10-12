namespace TopupLedger.Domain.Topups;

public enum TopupStatus
{
    Pending = 1,
    Success = 2,
    Failed = 3,
    Expired = 4
}

public static class TopupStatusNames
{
    public static string ToApi(TopupStatus status) => status switch
    {
        TopupStatus.Pending => "PENDING",
        TopupStatus.Success => "SUCCESS",
        TopupStatus.Failed => "FAILED",
        TopupStatus.Expired => "EXPIRED",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public static bool TryParse(string? text, out TopupStatus status)
    {
        status = default;
        switch (text)
        {
            case "PENDING": status = TopupStatus.Pending; return true;
            case "SUCCESS": status = TopupStatus.Success; return true;
            case "FAILED": status = TopupStatus.Failed; return true;
            case "EXPIRED": status = TopupStatus.Expired; return true;
            default: return false;
        }
    }

    public static bool IsTerminal(TopupStatus status) =>
        status is TopupStatus.Success or TopupStatus.Failed or TopupStatus.Expired;
}

public enum CallbackDisposition
{
    Applied,
    Duplicate,
    IgnoredOutOfOrder,
    IgnoredTerminal,
    LateAfterExpiry
}
