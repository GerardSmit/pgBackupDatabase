using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Sequence configured with non-default INCREMENT BY, MINVALUE, MAXVALUE,
/// and CYCLE. DDL emission must preserve all four; without them, a
/// restored sequence behaves differently from the source.
/// </summary>
public sealed class SequenceBoundsTests
{
    private readonly PgContainerFixture _pg;

    public SequenceBoundsTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Sequence_With_Increment_Bounds_And_Cycle_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE SEQUENCE sb_seq " +
            "  INCREMENT BY 7 " +
            "  MINVALUE 100 " +
            "  MAXVALUE 1000 " +
            "  START WITH 100 " +
            "  CYCLE;" +
            "CREATE TABLE sb_t(n bigint DEFAULT nextval('sb_seq'));" +
            "INSERT INTO sb_t DEFAULT VALUES;" +    // n = 100
            "INSERT INTO sb_t DEFAULT VALUES;");   // n = 107

        var full = Helpers.BackupPath("sb");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "sb_" + Guid.NewGuid().ToString("N")[..8];
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

            // All bounds preserved.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT increment_by, min_value, max_value, cycle " +
                    "FROM pg_sequences WHERE sequencename = 'sb_seq'";
                await using var rdr = await c.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
                Assert.True(await rdr.ReadAsync(
                    TestContext.Current.CancellationToken));
                Assert.Equal(7L, rdr.GetInt64(0));
                Assert.Equal(100L, rdr.GetInt64(1));
                Assert.Equal(1000L, rdr.GetInt64(2));
                Assert.True(rdr.GetBoolean(3));
            }

            // last_value continues from where source left off.
            await using (var c = r.CreateCommand())
            {
                c.CommandText = "INSERT INTO sb_t DEFAULT VALUES RETURNING n";
                var n = (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.True(n >= 114,
                    $"Expected sequence to continue past 107, got {n}");
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
