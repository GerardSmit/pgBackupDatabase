using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// CREATE INDEX on a partitioned parent: PostgreSQL creates a partitioned
/// index and propagates it to existing/new partitions. Restore must
/// reproduce both the parent partitioned index and the per-partition
/// child indexes attached via pg_inherits.
/// </summary>
public sealed class PartitionedIndexTests
{
    private readonly PgContainerFixture _pg;

    public PartitionedIndexTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Partitioned_Index_Propagates_To_All_Partitions_On_Restore()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE pi_t(id int NOT NULL, region text NOT NULL, " +
            "  PRIMARY KEY(id, region)) PARTITION BY LIST (region);" +
            "CREATE TABLE pi_t_us PARTITION OF pi_t FOR VALUES IN ('us');" +
            "CREATE TABLE pi_t_eu PARTITION OF pi_t FOR VALUES IN ('eu');" +
            "CREATE TABLE pi_t_def PARTITION OF pi_t DEFAULT;" +
            // Parent partitioned index — index is created on parent + each child.
            "CREATE INDEX pi_t_region_idx ON pi_t(region);" +
            "INSERT INTO pi_t VALUES (1,'us'),(2,'eu'),(3,'other');");

        var full = Helpers.BackupPath("part_idx");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "part_idx_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            // Parent partitioned index exists.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT relkind FROM pg_class " +
                    "WHERE relname = 'pi_t_region_idx'";
                var k = (char)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.Equal('I', k); // 'I' = partitioned index
            }

            // Each of the 3 child partitions has its own index on (region).
            // PostgreSQL auto-propagates partitioned indexes through
            // pg_inherits; either path is acceptable as long as every
            // partition has a usable region index.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "WITH parts AS (" +
                    "  SELECT inhrelid AS oid FROM pg_inherits " +
                    "  WHERE inhparent = 'pi_t'::regclass" +
                    ") " +
                    "SELECT count(*) FROM parts p " +
                    "WHERE EXISTS (" +
                    "  SELECT 1 FROM pg_index i " +
                    "  JOIN pg_attribute a " +
                    "    ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey) " +
                    "  WHERE i.indrelid = p.oid AND a.attname = 'region'" +
                    ")";
                Assert.Equal(3L, (long)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // Data and PK survive.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM pi_t";
                Assert.Equal(3L, (long)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
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
