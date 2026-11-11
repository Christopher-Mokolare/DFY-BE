using DoForYou.API.Services;

namespace DoForYou.Tests;

public sealed class SouthAfricanBusinessCalendarTests
{
    [Fact]
    public void Review_target_skips_weekends_and_uses_eight_business_hours()
    {
        // Friday 6 November 2026, 15:00 UTC = 17:00 SAST.
        var submittedAt = new DateTime(2026, 11, 6, 15, 0, 0, DateTimeKind.Utc);

        var dueAt = SouthAfricanBusinessCalendar.AddBusinessHours(submittedAt, 8);

        // The next working day is Monday 9 November; the target is 17:00 SAST.
        Assert.Equal(new DateTime(2026, 11, 9, 15, 0, 0, DateTimeKind.Utc), dueAt);
    }

    [Fact]
    public void Review_target_skips_declared_south_african_public_holiday()
    {
        // Tuesday 3 November 2026, 10:00 UTC = 12:00 SAST.
        // Wednesday 4 November 2026 was declared a national public holiday.
        var submittedAt = new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);

        var dueAt = SouthAfricanBusinessCalendar.AddBusinessHours(submittedAt, 8);

        // Five working hours remain Tuesday; the final three run Thursday morning.
        Assert.Equal(new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc), dueAt);
    }

    [Fact]
    public void Submission_outside_business_hours_starts_next_business_day_at_nine()
    {
        // Saturday 7 November 2026 at 10:00 SAST.
        var submittedAt = new DateTime(2026, 11, 7, 8, 0, 0, DateTimeKind.Utc);

        var dueAt = SouthAfricanBusinessCalendar.AddBusinessHours(submittedAt, 8);

        Assert.Equal(new DateTime(2026, 11, 9, 15, 0, 0, DateTimeKind.Utc), dueAt);
    }
}
