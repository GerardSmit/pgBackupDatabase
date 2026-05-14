using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// PostgreSQL supports special timestamp values: 'infinity', '-infinity',
/// and the calendar extremes. The serializer must round-trip these
/// without corruption — they're commonly used as "never expires" /
/// "always valid" sentinels.
/// </summary>
public sealed class InfinityTimestampTests
{
    private readonly PgContainerFixture _pg;

    public InfinityTimestampTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Infinity_And_Calendar_Extremes_RoundTrip()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE infts(" +
            "  id int PRIMARY KEY," +
            "  ts timestamptz NOT NULL," +
            "  d date NOT NULL" +
            ");" +
            "INSERT INTO infts VALUES " +
            "  (1, '-infinity'::timestamptz, '-infinity'::date)," +
            "  (2, 'infinity'::timestamptz, 'infinity'::date)," +
            "  (3, '4713-01-01 BC'::timestamptz, '4713-01-01 BC'::date)," +
            "  (4, '294276-12-31'::timestamptz, '5874897-12-31'::date)," +
            "  (5, '2026-05-13 12:34:56.789012+00'::timestamptz, '2026-05-13'::date);");

        var full = Helpers.BackupPath("infts");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "infts_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
            cmd.Parameters.AddWithValue("files", new[] { full });
            cmd.Parameters.AddWithValue("tgt", target);
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
            NpgsqlConnection.ClearAllPools();

            await using var r = await _pg.ConnectToAsync(target);

            // -infinity / +infinity preserved.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT id FROM infts " +
                    "WHERE ts = '-infinity'::timestamptz " +
                    "  AND d = '-infinity'::date";
                Assert.Equal(1, (int)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT id FROM infts " +
                    "WHERE ts = 'infinity'::timestamptz " +
                    "  AND d = 'infinity'::date";
                Assert.Equal(2, (int)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // Microsecond-precision normal timestamp preserved bit-exact.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT to_char(ts AT TIME ZONE 'UTC', " +
                    "  'YYYY-MM-DD HH24:MI:SS.US') FROM infts WHERE id = 5";
                Assert.Equal("2026-05-13 12:34:56.789012",
                    (string)(await c.ExecuteScalarAsync(
                        TestContext.Current.CancellationToken))!);
            }

            // BC era preserved.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT (extract(year FROM d AT TIME ZONE 'UTC'))::int " +
                    "FROM infts WHERE id = 3";
                var y = (int)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.Equal(-4713, y); // 4713 BC → year -4713 (PG ISO)
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
