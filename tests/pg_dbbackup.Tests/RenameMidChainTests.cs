using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// ALTER TABLE ... RENAME and ALTER TABLE ... RENAME COLUMN mid-chain
/// must propagate through the ddl_log so the restored DB has the
/// final names — not the original ones.
/// </summary>
public sealed class RenameMidChainTests
{
    private readonly PgContainerFixture _pg;

    public RenameMidChainTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Rename_Table_And_Column_Mid_Chain_RoundTrips_Final_Names()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE rn_old(id int PRIMARY KEY, val_old text NOT NULL);" +
            "INSERT INTO rn_old VALUES (1, 'a'), (2, 'b');");

        var full = Helpers.BackupPath("rn");
        await src.BackupFullAsync(full, compress: false);

        // Rename column, then rename table.
        await src.ExecAsync(
            "ALTER TABLE rn_old RENAME COLUMN val_old TO val_new;" +
            "ALTER TABLE rn_old RENAME TO rn_new;" +
            "INSERT INTO rn_new VALUES (3, 'c');");

        var log = Helpers.BackupPath("rn_log");
        await src.BackupLogAsync(log, full, compress: false);
        await src.CloseAsync();

        var target = "rn_" + Guid.NewGuid().ToString("N")[..8];
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

            // Final names present.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM pg_class WHERE relname = 'rn_new' AND relkind = 'r'";
                Assert.Equal(1L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM pg_attribute " +
                    "WHERE attrelid = 'rn_new'::regclass AND attname = 'val_new'";
                Assert.Equal(1L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // Old names not present.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM pg_class WHERE relname = 'rn_old'";
                Assert.Equal(0L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // Data fully replayed.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT array_agg(val_new ORDER BY id) FROM rn_new";
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
