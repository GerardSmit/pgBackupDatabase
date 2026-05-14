using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// ALTER TYPE composite ADD ATTRIBUTE during the LOG window. Composite
/// types reshape underlying tables that use them; the DDL journal must
/// capture the ALTER TYPE and replay it cleanly.
/// </summary>
public sealed class CompositeTypeAlterTests
{
    private readonly PgContainerFixture _pg;

    public CompositeTypeAlterTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task AlterType_Composite_AddAttribute_During_Log_Replays()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TYPE point2 AS (x int, y int);" +
            "CREATE TABLE pt_t(id int PRIMARY KEY, p point2);" +
            "INSERT INTO pt_t VALUES (1, ROW(1,2)::point2);");

        var full = Helpers.BackupPath("cta_full");
        await src.BackupFullAsync(full);

        // CASCADE is required when the type is used by an existing table.
        await src.ExecAsync(
            "ALTER TYPE point2 ADD ATTRIBUTE z int CASCADE;");
        await src.ExecAsync(
            "INSERT INTO pt_t VALUES (2, ROW(10,20,30)::point2);");

        var log = Helpers.BackupPath("cta_log");
        await src.BackupLogAsync(log, basePath: full);
        await src.CloseAsync();

        var target = "cta_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full, log);
            await using var r = await _pg.ConnectToAsync(target);

            // Composite now has 3 attributes.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT array_agg(attname ORDER BY attnum) " +
                    "FROM pg_attribute a " +
                    "JOIN pg_class c ON c.oid = a.attrelid " +
                    "JOIN pg_type t ON t.typrelid = c.oid " +
                    "WHERE t.typname = 'point2' AND a.attnum > 0";
                var arr = (string[])(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.Equal(new[] { "x", "y", "z" }, arr);
            }

            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText = "SELECT (p).z FROM pt_t WHERE id = 2";
                Assert.Equal(30, (int)(await cmd.ExecuteScalarAsync(
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
