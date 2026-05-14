using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Per-column knobs: ALTER COLUMN SET STATISTICS / SET STORAGE. These
/// affect query planner and TOAST behavior; ops teams tune them per
/// column and they must survive restore.
/// </summary>
public sealed class ColumnStatisticsAndStorageTests
{
    private readonly PgContainerFixture _pg;

    public ColumnStatisticsAndStorageTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task SetStatistics_And_SetStorage_RoundTrip()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE css_t(id int PRIMARY KEY, tag text, payload text);" +
            "ALTER TABLE css_t ALTER COLUMN tag SET STATISTICS 750;" +
            "ALTER TABLE css_t ALTER COLUMN payload SET STORAGE EXTERNAL;");

        var full = Helpers.BackupPath("css");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "css_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT a.attname, " +
                "       COALESCE(a.attstattarget, -1) AS stat, " +
                "       a.attstorage " +
                "FROM pg_attribute a " +
                "WHERE a.attrelid = 'css_t'::regclass " +
                "  AND a.attname IN ('tag','payload') " +
                "ORDER BY a.attname";
            await using var rdr = await cmd.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            var got = new Dictionary<string, (int stat, char storage)>();
            while (await rdr.ReadAsync(TestContext.Current.CancellationToken))
            {
                got[rdr.GetString(0)] =
                    (rdr.GetInt32(1), rdr.GetChar(2));
            }
            Assert.Equal(750, got["tag"].stat);
            Assert.Equal('e', got["payload"].storage); // EXTERNAL
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
