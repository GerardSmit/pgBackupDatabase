using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// ALTER ROLE x IN DATABASE y SET param: per-role per-database GUCs.
/// Like ALTER DATABASE SET, these must survive restore-with-rename via
/// pg_db_role_setting and reference current_database() rather than the
/// source database name.
/// </summary>
public sealed class PerDatabaseRoleSettingsTests
{
    private readonly PgContainerFixture _pg;

    public PerDatabaseRoleSettingsTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Per_Role_Per_Db_Setting_Survives_Restore_With_Rename()
    {
        var role = "pdrs_user_" + Guid.NewGuid().ToString("N")[..6];
        await using (var admin = await _pg.AdminAsync())
        {
            await admin.ExecAsync(
                $"DO $$ BEGIN " +
                $"IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='{role}') " +
                $"  THEN CREATE ROLE {role}; END IF; END $$;");
        }

        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        var srcDb = src.Database!;
        await using (var admin = await _pg.AdminAsync())
        {
            await admin.ExecAsync(
                $"ALTER ROLE {role} IN DATABASE \"{srcDb}\" " +
                $"  SET statement_timeout = '12345ms';");
        }

        var full = Helpers.BackupPath("pdrs");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "pdrs_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT array_to_string(rs.setconfig, ',') " +
                "FROM pg_db_role_setting rs " +
                "JOIN pg_roles ro ON ro.oid = rs.setrole " +
                "JOIN pg_database db ON db.oid = rs.setdatabase " +
                "WHERE ro.rolname = @r AND db.datname = @db";
            cmd.Parameters.AddWithValue("r", role);
            cmd.Parameters.AddWithValue("db", target);
            var v = await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken);
            Assert.NotNull(v);
            Assert.NotEqual(DBNull.Value, v);
            Assert.Contains("statement_timeout", (string)v!);
            Assert.Contains("12345", (string)v!);
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
