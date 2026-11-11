namespace DoForYou.API.Services;

/// <summary>
/// South African business-hour calculations for DFY Document Review.
/// The review clock runs 09:00–17:00 SAST, Monday to Friday, excluding public holidays.
/// Refresh the built-in holiday calendar annually; additional dates may be supplied through
/// IdentityVerification:SouthAfricanPublicHolidays as comma-separated yyyy-MM-dd values.
/// </summary>
public static class SouthAfricanBusinessCalendar
{
    public static DateTime AddBusinessHours(DateTime utcDateTime, double businessHours, string? additionalHolidayDates = null)
    {
        var holidays = new HashSet<DateOnly>
        {
            new(2026, 1, 1), new(2026, 3, 21), new(2026, 4, 3), new(2026, 4, 6),
            new(2026, 4, 27), new(2026, 5, 1), new(2026, 6, 16), new(2026, 8, 10),
            new(2026, 9, 24), new(2026, 11, 4), new(2026, 12, 16), new(2026, 12, 25),
            new(2026, 12, 26),
            new(2027, 1, 1), new(2027, 3, 22), new(2027, 3, 26), new(2027, 3, 29),
            new(2027, 4, 27), new(2027, 5, 1), new(2027, 6, 16), new(2027, 8, 9),
            new(2027, 9, 24), new(2027, 12, 16), new(2027, 12, 25), new(2027, 12, 26),
            new(2027, 12, 27)
        };
        if (!string.IsNullOrWhiteSpace(additionalHolidayDates))
        {
            foreach (var value in additionalHolidayDates.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (DateOnly.TryParse(value, out var date)) holidays.Add(date);
        }

        // South Africa uses UTC+02:00 year-round.
        var local = DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc).AddHours(2);
        var remaining = Math.Max(0, businessHours);
        local = MoveToNextBusinessStartIfNeeded(local, holidays);

        while (remaining > 0)
        {
            var availableToday = 17d - local.TimeOfDay.TotalHours;
            if (remaining <= availableToday)
            {
                local = local.AddHours(remaining);
                remaining = 0;
            }
            else
            {
                remaining -= availableToday;
                local = NextBusinessStart(local, holidays);
            }
        }

        return DateTime.SpecifyKind(local.AddHours(-2), DateTimeKind.Utc);
    }

    private static DateTime MoveToNextBusinessStartIfNeeded(DateTime local, HashSet<DateOnly> holidays)
    {
        var date = DateOnly.FromDateTime(local);
        if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || holidays.Contains(date) ||
            local.TimeOfDay >= TimeSpan.FromHours(17))
            return NextBusinessStart(local, holidays);
        if (local.TimeOfDay < TimeSpan.FromHours(9))
            return local.Date.AddHours(9);
        return local;
    }

    private static DateTime NextBusinessStart(DateTime local, HashSet<DateOnly> holidays)
    {
        var next = local.Date.AddDays(1).AddHours(9);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || holidays.Contains(DateOnly.FromDateTime(next)))
            next = next.AddDays(1);
        return next;
    }
}
