using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Logical replication subscription definition (created in disabled +
/// no-slot-create form) must survive restore so an operator can later
/// re-enable it against a real publisher.
/// </summary>
public sealed class SubscriptionDefinitionTests
{
    private readonly PgContainerFixture _pg;

    public SubscriptionDefinitionTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Disabled_Subscription_With_Publication_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE sub_t(id int PRIMARY KEY, v text);" +
            "CREATE PUBLICATION sub_pub FOR TABLE sub_t;" +
            "CREATE SUBSCRIPTION sub_def " +
            "  CONNECTION 'host=127.0.0.1 dbname=does_not_exist user=nobody' " +
            "  PUBLICATION sub_pub " +
            "  WITH (connect = false, enabled = false, create_slot = false, " +
            "        slot_name = NONE);");

        var full = Helpers.BackupPath("sub_def");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "sub_def_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT subname, subenabled, " +
                "       array_to_string(subpublications, ',') " +
                "FROM pg_subscription WHERE subname = 'sub_def'";
            await using var rdr = await cmd.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal("sub_def", rdr.GetString(0));
            Assert.False(rdr.GetBoolean(1));
            Assert.Equal("sub_pub", rdr.GetString(2));
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
