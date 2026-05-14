using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class SimpleDifferentialTests
{
    private readonly PgContainerFixture _pg;

    public SimpleDifferentialTests(PgContainerFixture pg) => _pg = pg;

    private async Task<int> ReadDiffFileCountAsync(string path)
    {
        var bytes = await _pg.ReadContainerFileAsync(path);
        var hdr = Helpers.ReadHeader(bytes);
        return hdr.GetProperty("file_count").GetInt32();
    }

    [Fact]
    public async Task Diff_Only_Changed_Tables()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE a(id int PRIMARY KEY, v text);" +
            "CREATE TABLE b(id int PRIMARY KEY, v text);" +
            "CREATE TABLE c(id int PRIMARY KEY, v text);" +
            "INSERT INTO a VALUES (1,'a1');" +
            "INSERT INTO b VALUES (1,'b1');" +
            "INSERT INTO c VALUES (1,'c1');");

        var fullPath = Helpers.BackupPath();
        await src.BackupFullAsync(fullPath);

        await src.ExecAsync("INSERT INTO a VALUES (2,'a2');");

        var diffPath = Helpers.BackupPath();
        await src.BackupDiffAsync(diffPath, fullPath);

        var count = await ReadDiffFileCountAsync(diffPath);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Diff_New_Tables_Included()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE keep(id int PRIMARY KEY);" +
            "INSERT INTO keep VALUES (1);");

        var fullPath = Helpers.BackupPath();
        await src.BackupFullAsync(fullPath);

        await src.ExecAsync(
            "CREATE TABLE d(id int PRIMARY KEY, label text);" +
            "INSERT INTO d VALUES (10, 'new');");

        var diffPath = Helpers.BackupPath();
        await src.BackupDiffAsync(diffPath, fullPath);

        var count = await ReadDiffFileCountAsync(diffPath);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Diff_Empty_If_No_Changes()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY);" +
            "INSERT INTO t VALUES (1),(2),(3);");

        var fullPath = Helpers.BackupPath();
        await src.BackupFullAsync(fullPath);

        var diffPath = Helpers.BackupPath();
        await src.BackupDiffAsync(diffPath, fullPath);

        var count = await ReadDiffFileCountAsync(diffPath);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Restore_With_Diff_Applies_Both()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE a(id int PRIMARY KEY, v text);" +
            "CREATE TABLE b(id int PRIMARY KEY, v text);" +
            "CREATE TABLE c(id int PRIMARY KEY, v text);" +
            "INSERT INTO a VALUES (1,'a1'),(2,'a2'),(3,'a3');" +
            "INSERT INTO b VALUES (1,'b1'),(2,'b2'),(3,'b3');" +
            "INSERT INTO c VALUES (1,'c1'),(2,'c2'),(3,'c3');");

        var fullPath = Helpers.BackupPath();
        await src.BackupFullAsync(fullPath);

        await src.ExecAsync(
            "UPDATE a SET v = 'a1-mod' WHERE id = 1;" +
            "INSERT INTO a VALUES (4,'a4');");

        var diffPath = Helpers.BackupPath();
        await src.BackupDiffAsync(diffPath, fullPath);
        await src.CloseAsync();

        var target = "diffrestore_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using (var admin = await _pg.AdminAsync())
            await using (var cmd = admin.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbrestore(ARRAY[@p1, @p2]::text[], target_db := @tgt)";
                cmd.Parameters.AddWithValue("db", "ignored");
                cmd.Parameters.AddWithValue("p1", fullPath);
                cmd.Parameters.AddWithValue("p2", diffPath);
                cmd.Parameters.AddWithValue("tgt", target);
                cmd.CommandTimeout = 60;
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            NpgsqlConnection.ClearAllPools();

            await using var conn = await _pg.ConnectToAsync(target);
            await using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT count(*) FROM a";
                Assert.Equal(4L, (long)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }
            await using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT v FROM a WHERE id = 1";
                Assert.Equal("a1-mod", (string)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }
            await using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT count(*) FROM b";
                Assert.Equal(3L, (long)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }
            await using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT count(*) FROM c";
                Assert.Equal(3L, (long)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Restore_Rejects_Two_Diffs()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY, v text);" +
            "INSERT INTO t VALUES (1,'one');");

        var fullPath = Helpers.BackupPath();
        await src.BackupFullAsync(fullPath);

        await src.ExecAsync("INSERT INTO t VALUES (2,'two');");
        var diff1 = Helpers.BackupPath();
        await src.BackupDiffAsync(diff1, fullPath);

        await src.ExecAsync("INSERT INTO t VALUES (3,'three');");
        var diff2 = Helpers.BackupPath();
        await src.BackupDiffAsync(diff2, fullPath);
        await src.CloseAsync();

        var target = "twodiffs_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(ARRAY[@p1, @p2, @p3]::text[], target_db := @tgt)";
            cmd.Parameters.AddWithValue("db", "ignored");
            cmd.Parameters.AddWithValue("p1", fullPath);
            cmd.Parameters.AddWithValue("p2", diff1);
            cmd.Parameters.AddWithValue("p3", diff2);
            cmd.Parameters.AddWithValue("tgt", target);

            await Assert.ThrowsAsync<PostgresException>(
                () => cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
