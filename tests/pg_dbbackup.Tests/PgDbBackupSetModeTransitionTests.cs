using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Verify the pg_dbbackup_set_mode entry point: SIMPLE → FULL records
/// the mode in dbbackup.db_config; FULL → SIMPLE updates it back.
/// The replication slot itself is created lazily on the first FULL
/// backup, not at set_mode time.
/// </summary>
public sealed class PgDbBackupSetModeTransitionTests
{
    private readonly PgContainerFixture _pg;

    public PgDbBackupSetModeTransitionTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Simple_Full_Simple_Transition_Updates_DbConfig()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        var dbName = src.Database!;

        // Initial state: default mode reported as SIMPLE.
        Assert.Equal("simple", await GetModeAsync(src, dbName));

        // Transition to FULL — get_mode reflects the change.
        await src.SetModeFullAsync();
        Assert.Equal("full", await GetModeAsync(src, dbName));

        // A first FULL backup creates the logical replication slot.
        await src.ExecAsync("CREATE TABLE t(id int PRIMARY KEY); INSERT INTO t VALUES (1);");
        var full = Helpers.BackupPath("setmode");
        await src.BackupFullAsync(full, compress: false);

        Assert.True(await CountSlotsAsync(dbName) >= 1,
            "Expected at least one logical replication slot after FULL backup");

        // Transition back to SIMPLE. Slot is invalidated and dropped.
        await using (var cmd = src.CreateCommand())
        {
            cmd.CommandText =
                "SELECT dbbackup.pg_dbbackup_set_mode(@d, 'simple')";
            cmd.Parameters.AddWithValue("d", dbName);
            await cmd.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
        }
        Assert.Equal("simple", await GetModeAsync(src, dbName));
    }

    private static async Task<string> GetModeAsync(
        NpgsqlConnection conn, string db)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT dbbackup.pg_dbbackup_get_mode(@d)";
        cmd.Parameters.AddWithValue("d", db);
        return (string)(await cmd.ExecuteScalarAsync(
            TestContext.Current.CancellationToken))!;
    }

    private async Task<int> CountSlotsAsync(string db)
    {
        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT count(*) FROM pg_replication_slots " +
            "WHERE database = @d AND slot_name LIKE '_pg_dbbackup_%'";
        cmd.Parameters.AddWithValue("d", db);
        return (int)(long)(await cmd.ExecuteScalarAsync(
            TestContext.Current.CancellationToken))!;
    }
}
