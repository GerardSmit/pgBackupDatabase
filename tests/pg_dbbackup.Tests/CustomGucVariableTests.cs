using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Custom GUCs (namespaced as "app.something") set per-database via
/// ALTER DATABASE ... SET are commonly used to carry feature flags or
/// secrets to application code. They live in pg_db_role_setting and
/// must round-trip.
/// </summary>
public sealed class CustomGucVariableTests
{
    private readonly PgContainerFixture _pg;

    public CustomGucVariableTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Custom_App_Guc_Set_On_Database_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        var db = src.Database!;

        // Custom GUCs require dotted names; PG accepts any namespace.
        await using (var admin = await _pg.AdminAsync())
        await using (var cmd = admin.CreateCommand())
        {
            cmd.CommandText =
                $"ALTER DATABASE \"{db}\" SET app.tenant_id = '42'";
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var admin = await _pg.AdminAsync())
        await using (var cmd = admin.CreateCommand())
        {
            cmd.CommandText =
                $"ALTER DATABASE \"{db}\" SET app.feature_flag = 'on'";
            await cmd.ExecuteNonQueryAsync();
        }

        var full = Helpers.BackupPath("guc");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "guc_" + Guid.NewGuid().ToString("N")[..8];
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
            await using var c = r.CreateCommand();
            c.CommandText =
                "SELECT current_setting('app.tenant_id'), " +
                "       current_setting('app.feature_flag')";
            await using var rdr = await c.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            Assert.True(await rdr.ReadAsync(
                TestContext.Current.CancellationToken));
            Assert.Equal("42", rdr.GetString(0));
            Assert.Equal("on", rdr.GetString(1));
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
