using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Partitioned table with a DEFAULT partition catching unmatched
/// values. Routing must be preserved and the default partition's
/// content must round-trip.
/// </summary>
public sealed class DefaultPartitionTests
{
    private readonly PgContainerFixture _pg;

    public DefaultPartitionTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Default_Partition_Captures_Unmatched_Rows_After_Restore()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE dp_t(id int, region text NOT NULL) " +
            "  PARTITION BY LIST(region);" +
            "CREATE TABLE dp_eu PARTITION OF dp_t FOR VALUES IN ('eu');" +
            "CREATE TABLE dp_us PARTITION OF dp_t FOR VALUES IN ('us');" +
            "CREATE TABLE dp_other PARTITION OF dp_t DEFAULT;" +
            "INSERT INTO dp_t VALUES " +
            "  (1, 'eu'), (2, 'us'), (3, 'apac'), (4, 'sa');");

        var full = Helpers.BackupPath("dp");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "dp_" + Guid.NewGuid().ToString("N")[..8];
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

            // Default partition catches apac + sa.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT array_agg(region ORDER BY id) FROM dp_other";
                var regs = (string[])(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.Equal(new[] { "apac", "sa" }, regs);
            }

            // Routing still works: new unmatched region lands in default.
            await using (var c = r.CreateCommand())
            {
                c.CommandText = "INSERT INTO dp_t VALUES (5, 'me')";
                await c.ExecuteNonQueryAsync(
                    TestContext.Current.CancellationToken);
            }
            await using (var c = r.CreateCommand())
            {
                c.CommandText = "SELECT count(*) FROM dp_other";
                Assert.Equal(3L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
