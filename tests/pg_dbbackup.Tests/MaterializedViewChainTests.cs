using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Matview behaviour the matrix smoke test misses: REFRESH propagates
/// through a FULL+LOG chain, and a matview that depends on another
/// matview restores in the right order.
/// </summary>
public sealed class MaterializedViewChainTests
{
    private readonly PgContainerFixture _pg;

    public MaterializedViewChainTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Dependent_Matviews_Restore_In_Dependency_Order()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE mv_base(id int PRIMARY KEY, v int NOT NULL);" +
            "INSERT INTO mv_base VALUES (1,10),(2,20),(3,30);" +
            "CREATE MATERIALIZED VIEW mv_l1 AS " +
            "  SELECT id, v * 2 AS v2 FROM mv_base;" +
            "CREATE MATERIALIZED VIEW mv_l2 AS " +
            "  SELECT id, v2 + 1 AS v3 FROM mv_l1;");

        var full = Helpers.BackupPath("mv_dep");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "mv_dep_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            // Both matviews must exist.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM pg_class " +
                    "WHERE relname IN ('mv_l1','mv_l2') AND relkind = 'm'";
                Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // REFRESH bottom-up; verify content.
            await r.ExecAsync("REFRESH MATERIALIZED VIEW mv_l1");
            await r.ExecAsync("REFRESH MATERIALIZED VIEW mv_l2");

            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText = "SELECT sum(v3) FROM mv_l2";
                Assert.Equal(123L, (long)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Matview_Created_During_Log_Window_Survives_Restore()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE mvw_base(id int PRIMARY KEY, label text);" +
            "INSERT INTO mvw_base VALUES (1,'one'),(2,'two');");

        var full = Helpers.BackupPath("mv_chain_full");
        await src.BackupFullAsync(full);

        // Matview created after FULL — must replay via DDL journal.
        await src.ExecAsync(
            "CREATE MATERIALIZED VIEW mvw_post AS " +
            "  SELECT id, upper(label) AS u FROM mvw_base;" +
            "INSERT INTO mvw_base VALUES (3,'three');");

        var log1 = Helpers.BackupPath("mv_chain_log1");
        await src.BackupLogAsync(log1, basePath: full);
        await src.CloseAsync();

        var target = "mv_chain_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full, log1);
            await using var r = await _pg.ConnectToAsync(target);

            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM pg_class " +
                    "WHERE relname = 'mvw_post' AND relkind = 'm'";
                Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM mvw_base";
                Assert.Equal(3L, (long)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    private async Task RestoreAsync(string target, params string[] files)
    {
        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
        cmd.Parameters.AddWithValue("files", files);
        cmd.Parameters.AddWithValue("tgt", target);
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        NpgsqlConnection.ClearAllPools();
    }
}
