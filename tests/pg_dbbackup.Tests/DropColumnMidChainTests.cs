using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// ALTER TABLE DROP COLUMN mid-chain. The restored DB must have the
/// final schema (column gone) and the data load must not try to write
/// to the missing column from earlier base.
/// </summary>
public sealed class DropColumnMidChainTests
{
    private readonly PgContainerFixture _pg;

    public DropColumnMidChainTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Drop_Column_Mid_Chain_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE dc_t(id int PRIMARY KEY, keep text, drop_me int);" +
            "INSERT INTO dc_t VALUES (1, 'a', 100), (2, 'b', 200);");

        var full = Helpers.BackupPath("dc");
        await src.BackupFullAsync(full, compress: false);

        await src.ExecAsync(
            "ALTER TABLE dc_t DROP COLUMN drop_me;" +
            "INSERT INTO dc_t(id, keep) VALUES (3, 'c');");

        var log = Helpers.BackupPath("dc_log");
        await src.BackupLogAsync(log, full, compress: false);
        await src.CloseAsync();

        var target = "dc_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
            cmd.Parameters.AddWithValue("files", new[] { full, log });
            cmd.Parameters.AddWithValue("tgt", target);
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
            NpgsqlConnection.ClearAllPools();

            await using var r = await _pg.ConnectToAsync(target);

            // Column gone.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM pg_attribute " +
                    "WHERE attrelid = 'dc_t'::regclass " +
                    "  AND attname = 'drop_me' AND NOT attisdropped";
                Assert.Equal(0L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // All 3 rows present with current schema.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT array_agg(keep ORDER BY id) FROM dc_t";
                var vals = (string[])(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.Equal(new[] { "a", "b", "c" }, vals);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
