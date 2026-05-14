using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// DST transitions create two kinds of edge cases for cron schedules:
///   - Spring-forward "gap": 02:00 → 03:00 leaps. A schedule that
///     names 02:30 in that local TZ does not exist on that calendar
///     date — pg_dbbackup_cron_due must not crash, and must not fire
///     twice when 03:30 arrives.
///   - Fall-back "overlap": 02:00 → 01:00 repeats. A schedule that
///     names 01:30 occurs twice in wall-clock; the cron primitive
///     must not deadlock and must return consistent results on each
///     repeat.
///
/// US Eastern transitions used here:
///   2026-03-08 02:00 → 03:00 (EST→EDT, spring forward)
///   2026-11-01 02:00 → 01:00 (EDT→EST, fall back)
/// </summary>
public sealed class SchedulerDstTransitionTests
{
    private readonly PgContainerFixture _pg;

    public SchedulerDstTransitionTests(PgContainerFixture pg) => _pg = pg;

    // Spring-forward: schedule "every day at 09:00 New York" must still
    // fire on 2026-03-08 09:00 NY local = 13:00 UTC (post-jump, EDT).
    [Theory]
    [InlineData("0 9 * * *", "America/New_York", "2026-03-08 13:00:00+00", true)]
    [InlineData("0 9 * * *", "America/New_York", "2026-03-08 14:00:00+00", false)]
    // Non-existent local time on spring-forward day. 02:30 EST does not
    // exist; querying it at 02:30 UTC-during-jump must not crash.
    [InlineData("30 2 * * *", "America/New_York", "2026-03-08 07:30:00+00", false)]
    [InlineData("30 2 * * *", "America/New_York", "2026-03-08 08:30:00+00", false)]
    // Fall-back: 01:30 happens twice locally. Both wall-clock instants
    // must return a consistent answer; neither call must error.
    [InlineData("30 1 * * *", "America/New_York", "2026-11-01 05:30:00+00", true)]
    [InlineData("30 1 * * *", "America/New_York", "2026-11-01 06:30:00+00", true)]
    // Same schedule, one second past — must NOT be due.
    [InlineData("30 1 * * *", "America/New_York", "2026-11-01 05:31:00+00", false)]
    [InlineData("30 1 * * *", "America/New_York", "2026-11-01 06:31:00+00", false)]
    public async Task CronDue_Survives_Dst_Transitions(
        string cron, string tz, string at, bool expected)
    {
        var dt = DateTime.Parse(at,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal |
            System.Globalization.DateTimeStyles.AssumeUniversal);

        await using var conn = await _pg.AdminAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbbackup_cron_due(@c, @tz, @at)";
        cmd.Parameters.AddWithValue("c", cron);
        cmd.Parameters.AddWithValue("tz", tz);
        cmd.Parameters.AddWithValue("at", dt);
        var got = (bool)(await cmd.ExecuteScalarAsync(
            TestContext.Current.CancellationToken))!;
        Assert.Equal(expected, got);
    }

    [Fact]
    public async Task CronDue_Same_Schedule_Across_DST_Forward_Stays_Stable_For_Distant_Hours()
    {
        // A noon-NY schedule must fire whether the date is pre- or post-
        // spring-forward. 12:00 NY local = 17:00 UTC (EST winter) or
        // 16:00 UTC (EDT summer).
        await using var conn = await _pg.AdminAsync();

        var winter = await CronDueAsync(conn, "0 12 * * *",
            "America/New_York", "2026-03-07 17:00:00+00"); // EST
        Assert.True(winter);

        var summer = await CronDueAsync(conn, "0 12 * * *",
            "America/New_York", "2026-03-09 16:00:00+00"); // EDT
        Assert.True(summer);

        // The "old" UTC offset must NOT also count.
        var staleWinter = await CronDueAsync(conn, "0 12 * * *",
            "America/New_York", "2026-03-09 17:00:00+00");
        Assert.False(staleWinter);
        var staleSummer = await CronDueAsync(conn, "0 12 * * *",
            "America/New_York", "2026-03-07 16:00:00+00");
        Assert.False(staleSummer);
    }

    private static async Task<bool> CronDueAsync(
        Npgsql.NpgsqlConnection conn, string cron, string tz, string atIso)
    {
        var dt = DateTime.Parse(atIso,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal |
            System.Globalization.DateTimeStyles.AssumeUniversal);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbbackup_cron_due(@c, @tz, @at)";
        cmd.Parameters.AddWithValue("c", cron);
        cmd.Parameters.AddWithValue("tz", tz);
        cmd.Parameters.AddWithValue("at", dt);
        return (bool)(await cmd.ExecuteScalarAsync(
            TestContext.Current.CancellationToken))!;
    }
}
