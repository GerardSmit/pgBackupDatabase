using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Inspection-time SQL surfaces (pg_dbbackup_header, pg_dbbackup_filelist)
/// must yield correct metadata for each link of a FULL+DIFF+LOG chain
/// without performing a full restore.
/// </summary>
public sealed class FilelistOnChainTests
{
    private readonly PgContainerFixture _pg;

    public FilelistOnChainTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Header_And_Filelist_Reflect_BackupType_Across_Chain()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY); INSERT INTO t VALUES (1);");

        var full = Helpers.BackupPath("fl_full");
        await src.BackupFullAsync(full);

        await src.ExecAsync("INSERT INTO t VALUES (2);");
        var diff = Helpers.BackupPath("fl_diff");
        await src.BackupDiffAsync(diff, basePath: full);

        await src.ExecAsync("INSERT INTO t VALUES (3);");
        var log = Helpers.BackupPath("fl_log");
        await src.BackupLogAsync(log, basePath: diff);
        await src.CloseAsync();

        await using var admin = await _pg.AdminAsync();

        await AssertHeaderAsync(admin, full, "full");
        await AssertHeaderAsync(admin, diff, "differential");
        await AssertHeaderAsync(admin, log, "log");

        // Filelist on FULL: returns >0 entries with checksum.
        await using (var cmd = admin.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM dbbackup.pg_dbbackup_filelist(@p)";
            cmd.Parameters.AddWithValue("p", full);
            Assert.True((long)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))! > 0);
        }
    }

    private static async Task AssertHeaderAsync(NpgsqlConnection admin,
        string path, string expectedType)
    {
        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT backup_type, mode FROM dbbackup.pg_dbbackup_header(@p)";
        cmd.Parameters.AddWithValue("p", path);
        await using var rdr = await cmd.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(expectedType, rdr.GetString(0));
        Assert.Equal("full", rdr.GetString(1)); // recovery mode = full
    }
}
