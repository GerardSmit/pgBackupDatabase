using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// TRUNCATE ... CASCADE empties parent + FK-referencing children in
/// one statement. The logical decoding stream represents this as a
/// TRUNCATE message; mid-chain replay must reproduce the empty state
/// on the restored target.
/// </summary>
public sealed class TruncateCascadeChainTests
{
    private readonly PgContainerFixture _pg;

    public TruncateCascadeChainTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Truncate_Cascade_Replays_Across_Log_Chain()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE tc_parent(id int PRIMARY KEY);" +
            "CREATE TABLE tc_child(" +
            "  id int PRIMARY KEY," +
            "  p int REFERENCES tc_parent(id) ON DELETE CASCADE);" +
            "ALTER TABLE tc_parent REPLICA IDENTITY FULL;" +
            "ALTER TABLE tc_child REPLICA IDENTITY FULL;" +
            "INSERT INTO tc_parent VALUES (1), (2), (3);" +
            "INSERT INTO tc_child VALUES (10, 1), (11, 1), (12, 2), (13, 3);");

        var full = Helpers.BackupPath("tc");
        await src.BackupFullAsync(full, compress: false);

        // TRUNCATE CASCADE empties both.
        await src.ExecAsync("TRUNCATE tc_parent CASCADE;");
        // Repopulate with different rows to make replay distinguishable.
        await src.ExecAsync(
            "INSERT INTO tc_parent VALUES (100);" +
            "INSERT INTO tc_child VALUES (1000, 100);");

        var log = Helpers.BackupPath("tc_log");
        await src.BackupLogAsync(log, full, compress: false);
        await src.CloseAsync();

        var target = "tc_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
            cmd.Parameters.AddWithValue("files", new[] { full, log });
            cmd.Parameters.AddWithValue("tgt", target);
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
            NpgsqlConnection.ClearAllPools();

            await using var r = await _pg.ConnectToAsync(target);

            // Only post-truncate rows survive.
            await using (var c = r.CreateCommand())
            {
                c.CommandText = "SELECT array_agg(id ORDER BY id) FROM tc_parent";
                var ids = (int[])(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.Equal(new[] { 100 }, ids);
            }
            await using (var c = r.CreateCommand())
            {
                c.CommandText = "SELECT array_agg(id ORDER BY id) FROM tc_child";
                var ids = (int[])(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.Equal(new[] { 1000 }, ids);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
