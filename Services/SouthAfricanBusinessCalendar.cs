using System.Security.Cryptography;
using System.Text;

namespace DoForYou.API.Services;

/// <summary>
/// South African business-day calculations for DFY Document Review.
/// Refresh the built-in holiday calendar annually; extra dates may be supplied
/// through IdentityVerification:SouthAfricanPublicHolidays as comma-separated yyyy-MM-dd values.
/// </summary>
public static class SouthAfricanBusinessCalendar
{
    public static DateTime AddBusinessDays(DateTime utcDateTime, int businessDays, string? additionalHolidayDates = null)
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

        // South Africa uses UTC+02:00 year-round. Calculate on the local calendar date,
        // then return the resulting instant in UTC.
        var local = DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc).AddHours(2);
        var added = 0;
        while (added < Math.Max(0, businessDays))
        {
            local = local.AddDays(1);
            var date = DateOnly.FromDateTime(local);
            if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || holidays.Contains(date))
                continue;
            added++;
        }
        return DateTime.SpecifyKind(local.AddHours(-2), DateTimeKind.Utc);
    }
}
