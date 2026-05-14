using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Scale: a database with hundreds of tables. DDL emission must not
/// blow up the SPI plan cache or hit catalog hot loops with O(N^2)
/// behavior. Backup + restore must complete in reasonable time and
/// the restored DB must have all tables.
/// </summary>
public sealed class ManyTablesScaleTests
{
    private readonly PgContainerFixture _pg;

    public ManyTablesScaleTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Five_Hundred_Tables_RoundTrip_In_Reasonable_Time()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();

        const int N = 500;
        // Build the DDL as a single statement.
        var sb = new System.Text.StringBuilder(N * 80);
        for (var i = 0; i < N; i++)
            sb.AppendFormat("CREATE TABLE t_{0:000}(id int PRIMARY KEY, v text);", i);
        sb.AppendFormat("INSERT INTO t_000 VALUES (1, 'first');");
        sb.AppendFormat("INSERT INTO t_{0:000} VALUES (42, 'last');", N - 1);
        await src.ExecAsync(sb.ToString(), timeoutSeconds: 240);

        var full = Helpers.BackupPath("many");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await src.BackupFullAsync(full, compress: true, commandTimeoutSeconds: 600);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromMinutes(5),
            $"Backup of {N} empty tables took {sw.Elapsed.TotalSeconds:N1}s — too slow");
        await src.CloseAsync();

        var target = "many_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
            cmd.Parameters.AddWithValue("files", new[] { full });
            cmd.Parameters.AddWithValue("tgt", target);
            cmd.CommandTimeout = 600;
            await cmd.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
            NpgsqlConnection.ClearAllPools();

            await using var r = await _pg.ConnectToAsync(target);
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM pg_class " +
                    "WHERE relkind = 'r' AND relname LIKE 't\\_%' ESCAPE '\\'";
                Assert.Equal((long)N, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
            await using (var c = r.CreateCommand())
            {
                c.CommandText = "SELECT v FROM t_000 WHERE id = 1";
                Assert.Equal("first", (string)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
            await using (var c = r.CreateCommand())
            {
                c.CommandText = $"SELECT v FROM t_{N - 1:000} WHERE id = 42";
                Assert.Equal("last", (string)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
