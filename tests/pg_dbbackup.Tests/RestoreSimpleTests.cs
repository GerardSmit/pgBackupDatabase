using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class RestoreSimpleTests
{
    private readonly PgContainerFixture _pg;

    public RestoreSimpleTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Restore_Roundtrip_Basic()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE widgets(id int PRIMARY KEY, name text);" +
            "INSERT INTO widgets SELECT g, 'name_' || g FROM generate_series(1, 100) g;");

        var path = Helpers.BackupPath();
        await src.BackupFullAsync(path, compress: true);
        await src.CloseAsync();

        var target = "restored_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using (var admin = await _pg.AdminAsync())
            await using (var cmd = admin.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbrestore(ARRAY[@p]::text[], target_db := @tgt)";
                cmd.Parameters.AddWithValue("db", "ignored");
                cmd.Parameters.AddWithValue("p", path);
                cmd.Parameters.AddWithValue("tgt", target);
                cmd.CommandTimeout = 60;
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            NpgsqlConnection.ClearAllPools();

            await using var conn = await _pg.ConnectToAsync(target);
            await using var c = conn.CreateCommand();
            c.CommandText = "SELECT count(*) FROM widgets";
            c.CommandTimeout = 30;
            var n = (long)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            Assert.Equal(100L, n);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Restore_Replaces_Existing()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE foo(id int PRIMARY KEY, label text);" +
            "INSERT INTO foo VALUES (1,'a'),(2,'b'),(3,'c'),(4,'d'),(5,'e');");

        var path = Helpers.BackupPath();
        await src.BackupFullAsync(path, compress: true);
        await src.CloseAsync();

        var target = "preexisting_" + Guid.NewGuid().ToString("N")[..8];

        await using (var admin = await _pg.AdminAsync())
        await using (var create = admin.CreateCommand())
        {
            create.CommandText = $"CREATE DATABASE \"{target}\"";
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        try
        {
            {
                var builder = new NpgsqlConnectionStringBuilder(_pg.ConnectionString)
                {
                    Database = target,
                    Pooling = false,
                };
                await using var preconn = new NpgsqlConnection(builder.ConnectionString);
                await preconn.OpenAsync(TestContext.Current.CancellationToken);
                await preconn.ExecAsync(
                    "CREATE TABLE bar(x int, payload text); " +
                    "INSERT INTO bar VALUES (99, 'gone');");
                await preconn.CloseAsync();
            }

            await using (var admin = await _pg.AdminAsync())
            await using (var cmd = admin.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbrestore(ARRAY[@p]::text[], target_db := @tgt)";
                cmd.Parameters.AddWithValue("db", "ignored");
                cmd.Parameters.AddWithValue("p", path);
                cmd.Parameters.AddWithValue("tgt", target);
                cmd.CommandTimeout = 60;
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            NpgsqlConnection.ClearAllPools();

            await using var conn = await _pg.ConnectToAsync(target);
            await using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT count(*) FROM foo";
                c.CommandTimeout = 30;
                Assert.Equal(5L, (long)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            await using (var c = conn.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM pg_class WHERE relname = 'bar' AND relkind = 'r'";
                c.CommandTimeout = 30;
                Assert.Equal(0L, (long)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Restore_Wrong_Password_Fails()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int);INSERT INTO t SELECT g FROM generate_series(1,10) g;");

        var path = Helpers.BackupPath();
        await src.BackupFullAsync(path, compress: true, password: "secret");
        await src.CloseAsync();

        var target = "wrongpw_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(ARRAY[@p]::text[], target_db := @tgt, password := @pw)";
            cmd.Parameters.AddWithValue("db", "ignored");
            cmd.Parameters.AddWithValue("p", path);
            cmd.Parameters.AddWithValue("tgt", target);
            cmd.Parameters.AddWithValue("pw", "wrong");

            await Assert.ThrowsAsync<PostgresException>(
                () => cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            await _pg.DropDbAsync(target);
        }
    }

    [Fact]
    public async Task Restore_Cleans_Up_On_Failure()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int); INSERT INTO t SELECT g FROM generate_series(1,50) g;");

        var path = Helpers.BackupPath();
        await src.BackupFullAsync(path, compress: true);
        await src.CloseAsync();

        var trunc = await _pg.ShellAsync(
            $"set -e; sz=$(stat -c %s {path}); newsz=$((sz - 200)); " +
            $"dd if={path} of={path}.t bs=1 count=$newsz status=none; mv {path}.t {path}");
        Assert.Equal(0, trunc.ExitCode);

        var target = "shouldfail_" + Guid.NewGuid().ToString("N")[..8];

        await using (var admin = await _pg.AdminAsync())
        await using (var cmd = admin.CreateCommand())
        {
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(ARRAY[@p]::text[], target_db := @tgt)";
            cmd.Parameters.AddWithValue("db", "ignored");
            cmd.Parameters.AddWithValue("p", path);
            cmd.Parameters.AddWithValue("tgt", target);

            await Assert.ThrowsAsync<PostgresException>(
                () => cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        Assert.False(await _pg.DbExistsAsync(target),
            "target DB unexpectedly exists after failed restore");

        await using (var admin = await _pg.AdminAsync())
        await using (var cmd = admin.CreateCommand())
        {
            cmd.CommandText =
                "SELECT count(*) FROM pg_database WHERE datname LIKE '_pg_dbbackup_restore_%'";
            var n = (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            Assert.Equal(0L, n);
        }
    }
}
