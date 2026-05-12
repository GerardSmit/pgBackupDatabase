using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class InspectTests
{
    private readonly PgContainerFixture _pg;

    public InspectTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Header_Reports_Simple_Full()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        var dbName = conn.Database!;
        await conn.ExecAsync("CREATE TABLE widgets(id int PRIMARY KEY);");

        var path = Helpers.BackupPath("inspect");
        await conn.BackupFullAsync(path);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT backup_type, mode, db_name, db_oid, compressed, encrypted " +
            "FROM dbbackup.pg_dbbackup_header(@path)";
        cmd.Parameters.AddWithValue("path", path);

        await using var rdr = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("full", rdr.GetString(0));
        Assert.Equal("simple", rdr.GetString(1));
        Assert.Equal(dbName, rdr.GetString(2));
        Assert.True(rdr.GetFieldValue<uint>(3) > 0);
        Assert.False(rdr.GetBoolean(4));
        Assert.False(rdr.GetBoolean(5));
    }

    [Fact]
    public async Task Header_Reports_Full_Differential()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.SetModeFullAsync();
        await conn.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY, v text);" +
            "INSERT INTO t SELECT g, 'r'||g FROM generate_series(1, 50) g;");

        var fullPath = Helpers.BackupPath("inspect");
        await conn.BackupFullAsync(fullPath);

        await conn.ExecAsync("INSERT INTO t VALUES (200, 'late');");

        var diffPath = Helpers.BackupPath("inspect");
        await conn.BackupDiffAsync(diffPath, fullPath);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT backup_type, mode, base_lsn::text " +
            "FROM dbbackup.pg_dbbackup_header(@path)";
        cmd.Parameters.AddWithValue("path", diffPath);

        await using var rdr = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("differential", rdr.GetString(0));
        Assert.Equal("full", rdr.GetString(1));
        var baseLsn = rdr.GetString(2);
        Assert.NotEqual("0/0", baseLsn);
    }

    [Fact]
    public async Task Header_Reports_Compressed_Encrypted()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.ExecAsync("CREATE TABLE t(id int);");

        var path = Helpers.BackupPath("inspect");
        await conn.BackupFullAsync(path, compress: true, password: "secret123");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT compressed, encrypted " +
            "FROM dbbackup.pg_dbbackup_header(@path)";
        cmd.Parameters.AddWithValue("path", path);

        await using var rdr = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
        Assert.True(rdr.GetBoolean(0));
        Assert.True(rdr.GetBoolean(1));
    }

    [Fact]
    public async Task Filelist_Returns_Entries_For_Simple_Full()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.ExecAsync(
            "CREATE TABLE widgets(id int PRIMARY KEY);" +
            "INSERT INTO widgets SELECT generate_series(1, 5);" +
            "CREATE TABLE gadgets(id int PRIMARY KEY);" +
            "INSERT INTO gadgets SELECT generate_series(1, 5);");

        var path = Helpers.BackupPath("inspect");
        await conn.BackupFullAsync(path);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT file_path, file_size, checksum " +
            "FROM dbbackup.pg_dbbackup_filelist(@path)";
        cmd.Parameters.AddWithValue("path", path);

        var paths = new List<string>();
        await using (var rdr = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await rdr.ReadAsync(TestContext.Current.CancellationToken))
            {
                paths.Add(rdr.GetString(0));
            }
        }

        Assert.Contains(paths, p => p.Contains("widgets"));
        Assert.Contains(paths, p => p.Contains("gadgets"));
    }

    [Fact]
    public async Task Filelist_Entries_Have_Sha256_Checksums()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.ExecAsync(
            "CREATE TABLE t(id int);" +
            "INSERT INTO t SELECT generate_series(1, 10);");

        var path = Helpers.BackupPath("inspect");
        await conn.BackupFullAsync(path);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT checksum FROM dbbackup.pg_dbbackup_filelist(@path)";
        cmd.Parameters.AddWithValue("path", path);

        int rowCount = 0;
        await using var rdr = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await rdr.ReadAsync(TestContext.Current.CancellationToken))
        {
            var checksum = rdr.GetString(0);
            Assert.Equal(64, checksum.Length);
            Assert.Matches("^[0-9a-f]{64}$", checksum);
            rowCount++;
        }
        Assert.True(rowCount > 0, "expected at least one filelist row");
    }

    [Fact]
    public async Task Filelist_Encrypted_Requires_Password()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.ExecAsync(
            "CREATE TABLE t(id int);" +
            "INSERT INTO t SELECT generate_series(1, 5);");

        var path = Helpers.BackupPath("inspect");
        await conn.BackupFullAsync(path, compress: false, password: "topsecret");

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT count(*) FROM dbbackup.pg_dbbackup_filelist(@path)";
            cmd.Parameters.AddWithValue("path", path);
            await Assert.ThrowsAsync<PostgresException>(
                () => cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        await using (var cmd2 = conn.CreateCommand())
        {
            cmd2.CommandText =
                "SELECT count(*) FROM dbbackup.pg_dbbackup_filelist(@path, @pw)";
            cmd2.Parameters.AddWithValue("path", path);
            cmd2.Parameters.AddWithValue("pw", "topsecret");
            var n = (long)(await cmd2.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            Assert.True(n > 0);
        }
    }

    [Fact]
    public async Task Filelist_Wrong_Password_Fails()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.ExecAsync(
            "CREATE TABLE t(id int);" +
            "INSERT INTO t SELECT generate_series(1, 5);");

        var path = Helpers.BackupPath("inspect");
        await conn.BackupFullAsync(path, compress: false, password: "rightpw");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT count(*) FROM dbbackup.pg_dbbackup_filelist(@path, @pw)";
        cmd.Parameters.AddWithValue("path", path);
        cmd.Parameters.AddWithValue("pw", "wrongpw");
        await Assert.ThrowsAsync<PostgresException>(
            () => cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
