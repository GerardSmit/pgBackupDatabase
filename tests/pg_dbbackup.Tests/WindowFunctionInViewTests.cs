using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// View whose body uses window functions over a PARTITION BY clause.
/// Window aggregates appear in pg_proc.proisagg=false but proiswindow=
/// true; pg_get_viewdef must preserve the exact rewrite rule.
/// </summary>
public sealed class WindowFunctionInViewTests
{
    private readonly PgContainerFixture _pg;

    public WindowFunctionInViewTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task View_With_Window_Function_RoundTrips_Definition_And_Behaviour()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE wf_t(id int PRIMARY KEY, cat text NOT NULL, v int NOT NULL);" +
            "INSERT INTO wf_t VALUES " +
            "  (1,'a',10),(2,'a',20),(3,'a',30), " +
            "  (4,'b',100),(5,'b',200);" +
            "CREATE VIEW wf_v AS " +
            "  SELECT id, cat, v, " +
            "         rank() OVER (PARTITION BY cat ORDER BY v DESC) AS r, " +
            "         sum(v) OVER (PARTITION BY cat) AS cat_sum " +
            "  FROM wf_t;");

        var full = Helpers.BackupPath("wfv");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "wfv_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            // View definition includes window clause.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText = "SELECT pg_get_viewdef('wf_v', true)";
                var def = (string)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.Contains("PARTITION BY", def);
                Assert.Contains("rank", def, StringComparison.OrdinalIgnoreCase);
            }

            // Behaviour: top-of-partition row for cat='a' has r=1, v=30.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT id, r, cat_sum FROM wf_v " +
                    "WHERE cat = 'a' AND r = 1";
                await using var rdr = await cmd.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
                Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
                Assert.Equal(3, rdr.GetInt32(0));
                Assert.Equal(1L, rdr.GetInt64(1));
                Assert.Equal(60L, rdr.GetInt64(2));
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
