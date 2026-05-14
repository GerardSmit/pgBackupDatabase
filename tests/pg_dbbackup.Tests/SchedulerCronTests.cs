using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Direct coverage for the PL/pgSQL cron primitives behind
/// pg_dbbackup_run_due_schedules. The scheduler trusts these helpers to
/// turn a cron string into a "due now" boolean; mismatches here would
/// silently mis-fire or skip backups.
/// </summary>
public sealed class SchedulerCronTests
{
    private readonly PgContainerFixture _pg;

    public SchedulerCronTests(PgContainerFixture pg) => _pg = pg;

    // ----- pg_dbbackup_cron_field_matches -----

    [Theory]
    [InlineData("*", 0, 0, 59, true)]
    [InlineData("*", 59, 0, 59, true)]
    [InlineData("0", 0, 0, 59, true)]
    [InlineData("0", 1, 0, 59, false)]
    [InlineData("15", 15, 0, 59, true)]
    [InlineData("15", 14, 0, 59, false)]
    [InlineData("1,5,9", 1, 0, 59, true)]
    [InlineData("1,5,9", 5, 0, 59, true)]
    [InlineData("1,5,9", 9, 0, 59, true)]
    [InlineData("1,5,9", 3, 0, 59, false)]
    [InlineData("*/5", 0, 0, 59, true)]
    [InlineData("*/5", 5, 0, 59, true)]
    [InlineData("*/5", 7, 0, 59, false)]
    [InlineData("*/15", 30, 0, 59, true)]
    [InlineData("*/15", 14, 0, 59, false)]
    [InlineData("*/0", 0, 0, 59, false)]  // zero step rejected
    [InlineData("7", 0, 0, 7, true)]      // dow alias: 7 matches Sunday=0
    [InlineData("7", 7, 0, 7, true)]      // and matches the literal n=7 case
    [InlineData("not-a-number", 0, 0, 59, false)]
    public async Task CronFieldMatches_Cases(
        string field, int value, int minValue, int maxValue, bool expected)
    {
        await using var conn = await _pg.AdminAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbbackup_cron_field_matches(@f, @v, @lo, @hi)";
        cmd.Parameters.AddWithValue("f", field);
        cmd.Parameters.AddWithValue("v", value);
        cmd.Parameters.AddWithValue("lo", minValue);
        cmd.Parameters.AddWithValue("hi", maxValue);
        var got = (bool)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(expected, got);
    }

    // ----- pg_dbbackup_cron_due -----

    [Theory]
    [InlineData("* * * * *", "UTC", "2026-05-13 12:34:00+00", true)]
    [InlineData("0 12 * * *", "UTC", "2026-05-13 12:00:00+00", true)]
    [InlineData("0 12 * * *", "UTC", "2026-05-13 12:34:00+00", false)]
    [InlineData("*/15 * * * *", "UTC", "2026-05-13 00:15:00+00", true)]
    [InlineData("*/15 * * * *", "UTC", "2026-05-13 00:16:00+00", false)]
    [InlineData("0 9 * * 1,2,3,4,5", "UTC", "2026-05-13 09:00:00+00", true)]   // Wed
    [InlineData("0 9 * * 1,2,3,4,5", "UTC", "2026-05-16 09:00:00+00", false)]  // Sat
    [InlineData("0 0 1 1 *", "UTC", "2026-01-01 00:00:00+00", true)]
    [InlineData("0 0 1 1 *", "UTC", "2026-01-02 00:00:00+00", false)]
    public async Task CronDue_Cases(string cron, string tz, string at, bool expected)
    {
        var dt = DateTime.Parse(at, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal |
            System.Globalization.DateTimeStyles.AssumeUniversal);

        await using var conn = await _pg.AdminAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbbackup_cron_due(@c, @tz, @at)";
        cmd.Parameters.AddWithValue("c", cron);
        cmd.Parameters.AddWithValue("tz", tz);
        cmd.Parameters.AddWithValue("at", dt);
        var got = (bool)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(expected, got);
    }

    [Fact]
    public async Task CronDue_MalformedString_ReturnsFalse()
    {
        await using var conn = await _pg.AdminAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbbackup_cron_due(@c, 'UTC', '2026-05-13 12:00:00+00'::timestamptz)";
        cmd.Parameters.AddWithValue("c", "bogus");
        var got = (bool)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        Assert.False(got);
    }

    [Fact]
    public async Task CronDue_Honors_NonUtc_Timezone()
    {
        // 09:00 America/New_York is 13:00 UTC (EDT in May).
        await using var conn = await _pg.AdminAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbbackup_cron_due('0 9 * * *', 'America/New_York', " +
            "'2026-05-13 13:00:00+00'::timestamptz)";
        var got = (bool)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        Assert.True(got);
    }
}
