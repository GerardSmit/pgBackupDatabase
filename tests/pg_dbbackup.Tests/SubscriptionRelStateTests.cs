using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// pg_subscription_rel rows track per-table replication state for a
/// subscription. They reference srrelid (pg_class.oid) which differs
/// in the restored database. A backup should either skip these rows
/// (subscription comes back unsynced) or remap the table OIDs so the
/// restored subscription is internally consistent.
/// </summary>
public sealed class SubscriptionRelStateTests
{
    private readonly PgContainerFixture _pg;

    public SubscriptionRelStateTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Disabled_Subscription_Round_Trip_Has_Consistent_RelState()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE sr_a(id int PRIMARY KEY);" +
            "CREATE TABLE sr_b(id int PRIMARY KEY);" +
            "CREATE PUBLICATION sr_pub FOR TABLE sr_a, sr_b;" +
            "CREATE SUBSCRIPTION sr_sub " +
            "  CONNECTION 'host=127.0.0.1 dbname=nope user=nobody' " +
            "  PUBLICATION sr_pub " +
            "  WITH (connect = false, enabled = false, create_slot = false, " +
            "        slot_name = NONE);");

        var full = Helpers.BackupPath("sr_relstate");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "sr_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            // Subscription itself round-trips. pg_subscription is a
            // shared catalog; scope by current DB oid.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM pg_subscription " +
                    "WHERE subname = 'sr_sub' " +
                    "  AND subdbid = (SELECT oid FROM pg_database " +
                    "                 WHERE datname = current_database())";
                Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // pg_subscription_rel rows that DO exist must reference live
            // tables in the restored DB. (Backup may choose to emit none —
            // that is acceptable for a disabled, unsynced subscription.)
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM pg_subscription_rel sr " +
                    "JOIN pg_subscription s ON s.oid = sr.srsubid " +
                    "WHERE s.subname = 'sr_sub' " +
                    "  AND s.subdbid = (SELECT oid FROM pg_database " +
                    "                   WHERE datname = current_database()) " +
                    "  AND NOT EXISTS (SELECT 1 FROM pg_class c " +
                    "                    WHERE c.oid = sr.srrelid)";
                Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // Tables themselves are present.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM pg_class " +
                    "WHERE relname IN ('sr_a','sr_b') AND relkind = 'r'";
                Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync(
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
