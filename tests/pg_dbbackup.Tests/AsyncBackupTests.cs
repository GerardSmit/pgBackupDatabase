using System.Diagnostics;
using Npgsql;
using NpgsqlTypes;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class AsyncBackupTests
{
    private readonly PgContainerFixture _pg;

    public AsyncBackupTests(PgContainerFixture pg) => _pg = pg;

    private static async Task<Guid> StartAsyncBackupAsync(NpgsqlConnection conn,
        string path, string type = "full", bool compress = false,
        string? basePath = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbbackup_async(@db, @path, type := @type, " +
            "compress := @compress, base_filepath := @base)";
        cmd.Parameters.AddWithValue("db", conn.Database!);
        cmd.Parameters.AddWithValue("path", path);
        cmd.Parameters.AddWithValue("type", type);
        cmd.Parameters.AddWithValue("compress", compress);
        cmd.Parameters.Add(new NpgsqlParameter("base", NpgsqlDbType.Text)
        {
            Value = (object?)basePath ?? DBNull.Value,
        });
        cmd.CommandTimeout = 30;
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<(string status, int progress, string? err,
        DateTime? startedAt, DateTime? completedAt)> GetStatusAsync(
        NpgsqlConnection conn, Guid id)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM dbbackup.pg_dbbackup_status(@id)";
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException($"no status row for {id}");
        var status = reader.GetString(0);
        var progress = reader.GetInt32(1);
        var err = await reader.IsDBNullAsync(2) ? null : reader.GetString(2);
        DateTime? startedAt = await reader.IsDBNullAsync(3)
            ? null : reader.GetDateTime(3);
        DateTime? completedAt = await reader.IsDBNullAsync(4)
            ? null : reader.GetDateTime(4);
        return (status, progress, err, startedAt, completedAt);
    }

    private static async Task<string> PollUntilDoneAsync(NpgsqlConnection conn,
        Guid id, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var s = await GetStatusAsync(conn, id);
            if (s.status is "completed" or "failed") return s.status;
            await Task.Delay(100);
        }
        throw new TimeoutException(
            $"job {id} did not finish within {timeout}");
    }

    [Fact]
    public async Task AsyncBackup_Returns_Uuid_Immediately()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY);" +
            "INSERT INTO t SELECT g FROM generate_series(1, 50) g;");

        var path = Helpers.BackupPath("async");
        var sw = Stopwatch.StartNew();
        var id = await StartAsyncBackupAsync(conn, path);
        sw.Stop();

        Assert.NotEqual(Guid.Empty, id);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"async should return quickly, took {sw.Elapsed}");

        var final = await PollUntilDoneAsync(conn, id, TimeSpan.FromSeconds(60));
        Assert.Equal("completed", final);
    }

    [Fact]
    public async Task AsyncBackup_Completes_Successfully()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.ExecAsync(
            "CREATE TABLE thing(id int PRIMARY KEY, payload text);" +
            "INSERT INTO thing SELECT g, repeat('p', 50) " +
            "FROM generate_series(1, 200) g;");

        var path = Helpers.BackupPath("async");
        var id = await StartAsyncBackupAsync(conn, path);

        var finalStatus = await PollUntilDoneAsync(conn, id,
            TimeSpan.FromSeconds(90));
        Assert.Equal("completed", finalStatus);

        var s = await GetStatusAsync(conn, id);
        Assert.Equal(100, s.progress);
        Assert.NotNull(s.startedAt);
        Assert.NotNull(s.completedAt);
        Assert.Null(s.err);

        await using var verify = conn.CreateCommand();
        verify.CommandText = "SELECT is_valid FROM dbbackup.pg_dbbackup_verify(@p)";
        verify.Parameters.AddWithValue("p", path);
        var ok = (bool)(await verify.ExecuteScalarAsync())!;
        Assert.True(ok, "backup file should pass verify");
    }

    [Fact]
    public async Task AsyncBackup_Wait_Returns_Status()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.ExecAsync(
            "CREATE TABLE x(id int PRIMARY KEY);" +
            "INSERT INTO x SELECT g FROM generate_series(1, 20) g;");

        var path = Helpers.BackupPath("async");
        var id = await StartAsyncBackupAsync(conn, path);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT dbbackup.pg_dbbackup_wait(@id, 90)";
        cmd.Parameters.AddWithValue("id", id);
        cmd.CommandTimeout = 120;
        var status = (string)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal("completed", status);
    }

    [Fact]
    public async Task AsyncBackup_Wait_Times_Out()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();

        var id = Guid.NewGuid();
        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO dbbackup.backup_jobs " +
                "(backup_id, dbname, filepath, type, compress, has_password, " +
                " status, progress) " +
                "VALUES (@id, @db, '/tmp/x.bak', 'full', false, false, " +
                " 'running', 5)";
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("db", conn.Database!);
            await insert.ExecuteNonQueryAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT dbbackup.pg_dbbackup_wait(@id, 1)";
        cmd.Parameters.AddWithValue("id", id);
        cmd.CommandTimeout = 30;
        var sw = Stopwatch.StartNew();
        var status = (string)(await cmd.ExecuteScalarAsync())!;
        sw.Stop();

        Assert.True(status is "pending" or "running",
            $"expected pending/running on timeout, got {status}");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"wait should return near the 1s timeout, took {sw.Elapsed}");
    }

    [Fact]
    public async Task AsyncBackup_Failure_Captured_In_Status()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.ExecAsync("CREATE TABLE u(id int PRIMARY KEY);");

        var badPath = "/nonexistent_dir_xyz/should_fail.bak";
        var id = await StartAsyncBackupAsync(conn, badPath);

        var status = await PollUntilDoneAsync(conn, id,
            TimeSpan.FromSeconds(60));
        Assert.Equal("failed", status);

        var s = await GetStatusAsync(conn, id);
        Assert.Equal("failed", s.status);
        Assert.False(string.IsNullOrEmpty(s.err),
            "failed jobs must have a non-empty error_message");
    }

    [Fact]
    public async Task AsyncBackup_Multiple_Concurrent()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.ExecAsync(
            "CREATE TABLE batch(id int PRIMARY KEY, v text);" +
            "INSERT INTO batch SELECT g, 'v' || g FROM generate_series(1, 100) g;");

        var ids = new List<Guid>();
        for (int i = 0; i < 3; i++)
        {
            var path = Helpers.BackupPath($"async_c{i}");
            ids.Add(await StartAsyncBackupAsync(conn, path));
        }

        foreach (var id in ids)
        {
            var status = await PollUntilDoneAsync(conn, id,
                TimeSpan.FromSeconds(120));
            Assert.Equal("completed", status);
        }
    }
}
