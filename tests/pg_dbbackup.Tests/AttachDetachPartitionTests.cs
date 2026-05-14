using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// ATTACH PARTITION and DETACH PARTITION mid-chain. The restored DB
/// must reflect the final attachment state, including the data that
/// flowed in via the partition while attached.
/// </summary>
public sealed class AttachDetachPartitionTests
{
    private readonly PgContainerFixture _pg;

    public AttachDetachPartitionTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Attach_Then_Detach_Partition_Mid_Chain_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE adp_parent(id int NOT NULL, region text NOT NULL, " +
            "  PRIMARY KEY (id, region)) " +
            "  PARTITION BY LIST(region);" +
            "CREATE TABLE adp_us PARTITION OF adp_parent FOR VALUES IN ('us');" +
            "INSERT INTO adp_parent VALUES (1, 'us');");

        var full = Helpers.BackupPath("adp");
        await src.BackupFullAsync(full, compress: false);

        // Create a standalone, then attach it.
        await src.ExecAsync(
            "CREATE TABLE adp_eu_standalone(id int NOT NULL, region text NOT NULL " +
            "  CHECK (region = 'eu'), PRIMARY KEY (id, region));" +
            "INSERT INTO adp_eu_standalone VALUES (2, 'eu'), (3, 'eu');" +
            "ALTER TABLE adp_parent ATTACH PARTITION adp_eu_standalone " +
            "  FOR VALUES IN ('eu');" +
            "INSERT INTO adp_parent VALUES (4, 'eu');");

        var log = Helpers.BackupPath("adp_log");
        await src.BackupLogAsync(log, full, compress: false);
        await src.CloseAsync();

        var target = "adp_" + Guid.NewGuid().ToString("N")[..8];
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

            // adp_eu_standalone is attached as partition of adp_parent.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM pg_inherits i " +
                    "JOIN pg_class child ON child.oid = i.inhrelid " +
                    "JOIN pg_class parent ON parent.oid = i.inhparent " +
                    "WHERE parent.relname = 'adp_parent' " +
                    "  AND child.relname IN ('adp_us','adp_eu_standalone')";
                Assert.True(2L <= (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // SELECT from parent returns all rows including post-attach.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT array_agg(id ORDER BY id) FROM adp_parent";
                var ids = (int[])(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.Equal(new[] { 1, 2, 3, 4 }, ids);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
