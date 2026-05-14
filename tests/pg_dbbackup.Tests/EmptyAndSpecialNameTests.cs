using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Operational corner cases: backing up a DB with zero user tables, and
/// backing up a DB whose name contains characters that require quoting
/// (dash, uppercase). pg_dbbackup must not assume identifier safety.
/// </summary>
public sealed class EmptyAndSpecialNameTests
{
    private readonly PgContainerFixture _pg;

    public EmptyAndSpecialNameTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Empty_Database_RoundTrips_With_No_Tables()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        var full = Helpers.BackupPath("empty");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "empty_r_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, new[] { full });
            await using var r = await _pg.ConnectToAsync(target);
            var userTables = (long)(await ExecScalarAsync(r,
                "SELECT count(*) FROM pg_class c " +
                "JOIN pg_namespace n ON n.oid = c.relnamespace " +
                "WHERE c.relkind = 'r' " +
                "  AND n.nspname NOT IN ('pg_catalog','information_schema') " +
                "  AND n.nspname NOT LIKE 'pg\\_%' " +
                "  AND n.nspname <> 'dbbackup'"))!;
            Assert.Equal(0L, userTables);

            // Restored DB must be reachable; basic sanity ping.
            var one = (int)(await ExecScalarAsync(r, "SELECT 1"))!;
            Assert.Equal(1, one);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task DbName_With_Dash_And_MixedCase_Backups_And_Restores()
    {
        var srcName = "WeirdDB-" + Guid.NewGuid().ToString("N")[..6];
        await using (var admin = await _pg.AdminAsync())
        await using (var cmd = admin.CreateCommand())
        {
            cmd.CommandText = $"CREATE DATABASE \"{srcName}\"";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var full = Helpers.BackupPath("weirdname");
        try
        {
            await using (var src = await _pg.ConnectToAsync(srcName))
            {
                await src.ExecAsync("CREATE EXTENSION pg_dbbackup");
                await src.ExecAsync(
                    "CREATE TABLE t(id int PRIMARY KEY);" +
                    "INSERT INTO t VALUES (1), (2), (3);");
                await src.BackupFullAsync(full, compress: true);
            }

            var target = "WeirdR-" + Guid.NewGuid().ToString("N")[..6];
            try
            {
                await RestoreAsync(target, new[] { full });
                await using var r = await _pg.ConnectToAsync(target);
                var n = (long)(await ExecScalarAsync(r,
                    "SELECT count(*) FROM t"))!;
                Assert.Equal(3L, n);
            }
            finally
            {
                try { await _pg.DropDbAsync(target); } catch { }
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(srcName); } catch { }
        }
    }

    private async Task RestoreAsync(string target, string[] files)
    {
        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
        cmd.Parameters.AddWithValue("files", files);
        cmd.Parameters.AddWithValue("tgt", target);
        cmd.CommandTimeout = 180;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        NpgsqlConnection.ClearAllPools();
    }

    private static async Task<object?> ExecScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        return await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }
}
