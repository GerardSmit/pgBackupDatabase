using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// ALTER TABLE ... ALTER COLUMN ... TYPE ... USING during the LOG window:
/// data-rewriting ALTER that must be captured by the DDL journal and
/// replayed on the restored database before subsequent LOG INSERTs.
/// </summary>
public sealed class AlterColumnTypeMidChainTests
{
    private readonly PgContainerFixture _pg;

    public AlterColumnTypeMidChainTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task AlterColumnType_With_Using_Replays_Cleanly()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE act_t(id int PRIMARY KEY, v text NOT NULL);" +
            "INSERT INTO act_t VALUES (1,'10'),(2,'20'),(3,'30');");

        var full = Helpers.BackupPath("act_full");
        await src.BackupFullAsync(full);

        await src.ExecAsync(
            "ALTER TABLE act_t ALTER COLUMN v TYPE int USING v::int;");
        await src.ExecAsync("INSERT INTO act_t VALUES (4, 40);");

        var log = Helpers.BackupPath("act_log");
        await src.BackupLogAsync(log, basePath: full);
        await src.CloseAsync();

        var target = "act_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full, log);
            await using var r = await _pg.ConnectToAsync(target);

            // Column is now integer.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT data_type FROM information_schema.columns " +
                    "WHERE table_name = 'act_t' AND column_name = 'v'";
                Assert.Equal("integer", (string)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // All 4 rows present and arithmetic works on v.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText = "SELECT sum(v) FROM act_t";
                Assert.Equal(100L, (long)(await cmd.ExecuteScalarAsync(
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
