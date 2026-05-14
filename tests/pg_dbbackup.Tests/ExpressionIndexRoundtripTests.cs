using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Expression indexes (CREATE INDEX ON t (lower(name))) and partial
/// indexes (... WHERE active) need pg_get_indexdef-style emission for
/// the predicate and expression. A naive column-list emission would
/// lose these.
/// </summary>
public sealed class ExpressionIndexRoundtripTests
{
    private readonly PgContainerFixture _pg;

    public ExpressionIndexRoundtripTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Expression_And_Partial_Indexes_RoundTrip()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE ei_t(id int PRIMARY KEY, name text NOT NULL, active boolean);" +
            "CREATE INDEX ei_t_lower_name ON ei_t(lower(name));" +
            "CREATE INDEX ei_t_active_only ON ei_t(id) WHERE active;" +
            "CREATE UNIQUE INDEX ei_t_md5_name ON ei_t(md5(name));" +
            "INSERT INTO ei_t VALUES " +
            "  (1,'Alice', true), (2, 'BOB', false), (3, 'carol', true);");

        var full = Helpers.BackupPath("expridx");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "ei_" + Guid.NewGuid().ToString("N")[..8];
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
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT indexdef FROM pg_indexes " +
                    "WHERE tablename = 'ei_t' AND indexname = 'ei_t_lower_name'";
                var def = (string)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.Contains("lower(", def, StringComparison.OrdinalIgnoreCase);
            }
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT indexdef FROM pg_indexes " +
                    "WHERE tablename = 'ei_t' AND indexname = 'ei_t_active_only'";
                var def = (string)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.Contains("WHERE", def, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("active", def, StringComparison.OrdinalIgnoreCase);
            }
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM pg_indexes " +
                    "WHERE tablename = 'ei_t' AND indexname = 'ei_t_md5_name'";
                Assert.Equal(1L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
