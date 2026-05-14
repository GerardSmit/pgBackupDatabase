using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Diagnostic harness: drive each known-problematic type / DDL pattern
/// through FULL + LOG and surface the real PG errdetail (fixture now
/// includes Include Error Detail). Each test isolates one suspect.
/// </summary>
public sealed class DiagnoseFailuresTests
{
    private readonly PgContainerFixture _pg;

    public DiagnoseFailuresTests(PgContainerFixture pg) => _pg = pg;

    [Theory]
    [InlineData("bit4", "flag bit(4) NOT NULL", "B'1010'")]
    [InlineData("bitvar", "bv bit varying NOT NULL", "B'11001010'")]
    [InlineData("tstzrange", "tr tstzrange NOT NULL",
        "tstzrange('2026-01-01 00:00+00','2026-02-01 00:00+00','[)')")]
    [InlineData("arr2d", "a int[][] NOT NULL", "ARRAY[[1,2],[3,4]]")]
    [InlineData("composite", "c addr NOT NULL", "ROW('Elm Street','12345')")]
    [InlineData("money", "m money NOT NULL", "'$1,234.56'::money")]
    public async Task Single_Type_Log_Replay(string label, string column, string literal)
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        if (label == "composite")
            await src.ExecAsync("CREATE TYPE addr AS (street text, zip text);");
        await src.ExecAsync($"CREATE TABLE t(id int PRIMARY KEY, {column});");

        var full = Helpers.BackupPath($"diag_{label}_full");
        await src.BackupFullAsync(full, compress: false, commandTimeoutSeconds: 60);

        await src.ExecAsync($"INSERT INTO t VALUES (1, {literal});");

        var log = Helpers.BackupPath($"diag_{label}_log");
        await src.BackupLogAsync(log, full, compress: false, commandTimeoutSeconds: 60);
        await src.CloseAsync();

        var target = $"diag_{label}_" + Guid.NewGuid().ToString("N")[..6];
        try
        {
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
            cmd.Parameters.AddWithValue("files", new[] { full, log });
            cmd.Parameters.AddWithValue("tgt", target);
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            NpgsqlConnection.ClearAllPools();

            await using var r = await _pg.ConnectToAsync(target);
            await using var c = r.CreateCommand();
            c.CommandText = "SELECT count(*) FROM t";
            Assert.Equal(1L, (long)(await c.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task DropTable_Cascade_Schema_Replay()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE parent(id int PRIMARY KEY);" +
            "CREATE TABLE child(id int PRIMARY KEY, pid int REFERENCES parent(id));" +
            "INSERT INTO parent VALUES (1);" +
            "INSERT INTO child VALUES (10, 1);");

        var full = Helpers.BackupPath("diag_cascade");
        await src.BackupFullAsync(full, compress: false, commandTimeoutSeconds: 60);

        await src.ExecAsync("DROP TABLE parent CASCADE;");

        var log = Helpers.BackupPath("diag_cascade_log");
        await src.BackupLogAsync(log, full, compress: false, commandTimeoutSeconds: 60);
        await src.CloseAsync();

        var target = "diag_cascade_" + Guid.NewGuid().ToString("N")[..6];
        try
        {
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
            cmd.Parameters.AddWithValue("files", new[] { full, log });
            cmd.Parameters.AddWithValue("tgt", target);
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
