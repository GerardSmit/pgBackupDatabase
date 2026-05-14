using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Validates that backup machinery does not leak replication slots when
/// the source database is dropped right after a FULL backup completes.
/// </summary>
public sealed class ConcurrentSourceDropTests
{
    private readonly PgContainerFixture _pg;

    public ConcurrentSourceDropTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task DropDatabase_After_Full_Mode_Backup_Cleans_Slot_State()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        var dbName = src.Database!;
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY); INSERT INTO t VALUES (1);");

        var full = Helpers.BackupPath("cdrop");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();
        NpgsqlConnection.ClearAllPools();

        // Drop the source database WITH (FORCE) to terminate residual sessions.
        await _pg.DropDbAsync(dbName);

        // Verify there are no orphan replication slots tied to the dropped DB.
        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT count(*) FROM pg_replication_slots " +
            "WHERE database = @db";
        cmd.Parameters.AddWithValue("db", dbName);
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync(
            TestContext.Current.CancellationToken))!);

        // FULL backup file still restorable to a new target.
        var target = "cdrop_r_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using var cmd2 = admin.CreateCommand();
            cmd2.CommandText =
                "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
            cmd2.Parameters.AddWithValue("files", new[] { full });
            cmd2.Parameters.AddWithValue("tgt", target);
            cmd2.CommandTimeout = 120;
            await cmd2.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            NpgsqlConnection.ClearAllPools();

            await using var r = await _pg.ConnectToAsync(target);
            await using var c3 = r.CreateCommand();
            c3.CommandText = "SELECT count(*) FROM t";
            Assert.Equal(1L, (long)(await c3.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally { try { await _pg.DropDbAsync(target); } catch { } }
    }
}
