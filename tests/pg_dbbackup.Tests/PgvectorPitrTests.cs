using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// FULL-mode (logical PITR) coverage for the pgvector extension: FULL +
/// LOG round-trip and stop_at cutoff verification. Existing PgvectorTests
/// covers only SIMPLE-mode FULL.
/// </summary>
public sealed class PgvectorPitrTests
{
    private readonly PgWithExtensionsFixture _pg;

    public PgvectorPitrTests(PgWithExtensionsFixture pg) => _pg = pg;

    [Fact]
    public async Task Pgvector_FullPlusLog_RoundTrip()
    {
        if (!_pg.HasPgvector)
            Assert.Skip("vector (pgvector) extension not available");

        await using var src = await _pg.CreateFreshDbWithExtensionAsync("vector");
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE embeds(id int PRIMARY KEY, v vector(3));",
            timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO embeds VALUES (1, '[1,2,3]'), (2, '[4,5,6]');",
            timeoutSeconds: 60);

        var full = Helpers.BackupPath("pgvec_full");
        await src.BackupFullAsync(full, compress: true, commandTimeoutSeconds: 180);

        await src.ExecAsync(
            "INSERT INTO embeds VALUES (3, '[7,8,9]'), (4, '[0,0,0]');" +
            "UPDATE embeds SET v = '[10,10,10]' WHERE id = 1;",
            timeoutSeconds: 60);
        var log = Helpers.BackupPath("pgvec_log");
        await src.BackupLogAsync(log, full, compress: true, commandTimeoutSeconds: 180);
        await src.CloseAsync();

        var target = await RestoreAsync(null, full, log);
        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var conn = await _pg.ConnectToAsync(target);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*), max(id) FROM embeds";
                cmd.CommandTimeout = 60;
                await using var rdr = await cmd.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
                Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
                Assert.Equal(4L, rdr.GetInt64(0));
                Assert.Equal(4, rdr.GetInt32(1));
            }

            await using var v = conn.CreateCommand();
            v.CommandText = "SELECT v::text FROM embeds WHERE id = 1";
            Assert.Equal("[10,10,10]", (string)(await v.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Pgvector_Pitr_Stops_At_Timestamp()
    {
        if (!_pg.HasPgvector)
            Assert.Skip("vector (pgvector) extension not available");

        await using var src = await _pg.CreateFreshDbWithExtensionAsync("vector");
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE embeds(id int PRIMARY KEY, v vector(3));",
            timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO embeds VALUES (1, '[1,2,3]');", timeoutSeconds: 60);

        var full = Helpers.BackupPath("pgvec_pitr");
        await src.BackupFullAsync(full, compress: true, commandTimeoutSeconds: 180);

        await Task.Delay(1200, TestContext.Current.CancellationToken);
        await src.ExecAsync(
            "INSERT INTO embeds VALUES (2, '[4,5,6]');", timeoutSeconds: 60);

        DateTime cutoff;
        await using (var c = src.CreateCommand())
        {
            c.CommandText = "SELECT clock_timestamp()";
            cutoff = (DateTime)(await c.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
        }

        await Task.Delay(1200, TestContext.Current.CancellationToken);
        await src.ExecAsync(
            "INSERT INTO embeds VALUES (3, '[7,8,9]');", timeoutSeconds: 60);

        var log = Helpers.BackupPath("pgvec_pitr_log");
        await src.BackupLogAsync(log, full, compress: true, commandTimeoutSeconds: 180);
        await src.CloseAsync();

        var target = await RestoreAsync(cutoff, full, log);
        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var conn = await _pg.ConnectToAsync(target);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*), max(id) FROM embeds";
            cmd.CommandTimeout = 60;
            await using var rdr = await cmd.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(2L, rdr.GetInt64(0));
            Assert.Equal(2, rdr.GetInt32(1));
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    private async Task<string> RestoreAsync(DateTime? stopAt, params string[] files)
    {
        var target = "pgvec_pitr_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText = stopAt.HasValue
            ? "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt, stop_at := @stop)"
            : "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
        cmd.Parameters.AddWithValue("files", files);
        cmd.Parameters.AddWithValue("tgt", target);
        if (stopAt.HasValue) cmd.Parameters.AddWithValue("stop", stopAt.Value);
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return target;
    }
}
