using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Temporary tables (pg_temp_*) live only for the session and must NOT
/// appear in a logical backup. Their inclusion would be both wasteful
/// and broken on restore (the session's pg_temp_N schema does not
/// exist on the restored DB).
/// </summary>
public sealed class TempTableSkipTests
{
    private readonly PgContainerFixture _pg;

    public TempTableSkipTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Temp_Tables_Are_Not_Backed_Up()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        // Permanent table — must round-trip.
        await src.ExecAsync(
            "CREATE TABLE perm_t(id int PRIMARY KEY);" +
            "INSERT INTO perm_t VALUES (1), (2);");
        // Temp table on the same session.
        await src.ExecAsync(
            "CREATE TEMPORARY TABLE temp_t(id int PRIMARY KEY);" +
            "INSERT INTO temp_t VALUES (100), (200), (300);");

        var full = Helpers.BackupPath("temp");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "temp_" + Guid.NewGuid().ToString("N")[..8];
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

            // Permanent table survived.
            await using (var c = r.CreateCommand())
            {
                c.CommandText = "SELECT count(*) FROM perm_t";
                Assert.Equal(2L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // No temp table or pg_temp_* schema in the restored DB.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM pg_class " +
                    "WHERE relname = 'temp_t' AND relkind = 'r' AND relpersistence = 't'";
                Assert.Equal(0L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
