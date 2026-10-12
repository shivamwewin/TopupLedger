using TopupLedger.Domain.Money;

namespace TopupLedger.Domain.Time;

/// <summary>
/// Business day is the Dubai civil date, with an optional cut-off after which the
/// transaction belongs to the next business day. Dubai is fixed UTC+04:00 (no DST).
/// The machine time zone is never consulted.
/// A cut-off of 00:00 means the calendar date in Dubai (variant V0). Any later
/// cut-off rolls instants at or after that local time into the next date.
/// </summary>
public static class BusinessDayCalculator
{
    public static DateOnly ForInstant(DateTimeOffset utcInstant, TimeOnly cutoff)
    {
        var dubai = utcInstant.ToOffset(CommissionCalculator.DubaiOffset);
        var date = DateOnly.FromDateTime(dubai.DateTime);
        if (cutoff == TimeOnly.MinValue)
        {
            return date;
        }

        var localTime = TimeOnly.FromDateTime(dubai.DateTime);
        return localTime >= cutoff ? date.AddDays(1) : date;
    }
}
