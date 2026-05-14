using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class DatabaseGucTests
{
    private readonly PgContainerFixture _pg;

    public DatabaseGucTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Database_SearchPath_Persistence_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        var srcDb = src.Database!;
        await src.ExecAsync(
            "CREATE SCHEMA app_schema;" +
            "CREATE TABLE app_schema.t(id int PRIMARY KEY);" +
            "INSERT INTO app_schema.t VALUES (1);");
        await using (var admin = await _pg.AdminAsync())
        await using (var cmd = admin.CreateCommand())
        {
            cmd.CommandText =
                $"ALTER DATABASE \"{srcDb}\" SET search_path = 'app_schema,public'";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var full = Helpers.BackupPath("guc_sp");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "guc_sp_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText = "SHOW search_path";
            var sp = (string)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
            Assert.Contains("app_schema", sp);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Database_AlterSet_Guc_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        var srcDb = src.Database!;
        await using (var admin = await _pg.AdminAsync())
        await using (var cmd = admin.CreateCommand())
        {
            cmd.CommandText =
                $"ALTER DATABASE \"{srcDb}\" SET statement_timeout = '12345ms'";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await src.ExecAsync("CREATE TABLE t(id int PRIMARY KEY); INSERT INTO t VALUES(1);");

        var full = Helpers.BackupPath("guc_db");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "guc_db_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText = "SHOW statement_timeout";
            var s = (string)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
            Assert.Equal("12345ms", s);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    private async Task RestoreAsync(string target, string file)
    {
        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
        cmd.Parameters.AddWithValue("files", new[] { file });
        cmd.Parameters.AddWithValue("tgt", target);
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        NpgsqlConnection.ClearAllPools();
    }
}
