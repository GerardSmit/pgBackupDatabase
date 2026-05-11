using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class FullBackupTests
{
    private readonly PgContainerFixture _pg;

    public FullBackupTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Full_Backup_Creates_Bak_With_Data_And_Wal_Sections()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.SetModeFullAsync();
        await conn.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY, v text);" +
            "INSERT INTO t SELECT g, 'r' || g FROM generate_series(1, 50) g;");

        var path = Helpers.BackupPath("full");
        await conn.BackupFullAsync(path);

        var bytes = await _pg.ReadContainerFileAsync(path);
        var sections = Helpers.DecodeSections(bytes).ToList();

        Assert.Contains(sections, s => s.sectionType == Helpers.SectionData);
        Assert.Contains(sections, s => s.sectionType == Helpers.SectionWal);
    }

    [Fact]
    public async Task Full_Backup_Captures_Relation_Files()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.SetModeFullAsync();
        await conn.ExecAsync(
            "CREATE TABLE rels(id int PRIMARY KEY, payload text);" +
            "INSERT INTO rels SELECT g, repeat('p', 100) FROM generate_series(1, 100) g;");

        var path = Helpers.BackupPath("full");
        await conn.BackupFullAsync(path);

        var bytes = await _pg.ReadContainerFileAsync(path);
        var dataSection = Helpers.DecodeSections(bytes)
            .First(s => s.sectionType == Helpers.SectionData).data;
        var paths = Helpers.DecodeDataEntryPaths(dataSection);

        Assert.Contains(paths, p => p.StartsWith("base/"));
    }

    [Fact]
    public async Task Full_Backup_Captures_Pg_Filenode_Map()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.SetModeFullAsync();

        var path = Helpers.BackupPath("full");
        await conn.BackupFullAsync(path);

        var bytes = await _pg.ReadContainerFileAsync(path);
        var dataSection = Helpers.DecodeSections(bytes)
            .First(s => s.sectionType == Helpers.SectionData).data;
        var paths = Helpers.DecodeDataEntryPaths(dataSection);

        Assert.Contains(paths, p => p == "global/pg_filenode.map");
    }

    [Fact]
    public async Task Full_Backup_Header_Reports_Full_Mode()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.SetModeFullAsync();
        await conn.ExecAsync(
            "CREATE TABLE marker(id int PRIMARY KEY);" +
            "INSERT INTO marker VALUES (1),(2),(3);");

        var path = Helpers.BackupPath("full");
        await conn.BackupFullAsync(path);

        var bytes = await _pg.ReadContainerFileAsync(path);
        var hdr = Helpers.ReadHeader(bytes);

        Assert.Equal("full", hdr.GetProperty("mode").GetString());
        Assert.Equal("full", hdr.GetProperty("type").GetString());
        Assert.NotEqual("00000000/00000000", hdr.GetProperty("start_lsn").GetString());
        Assert.NotEqual("00000000/00000000", hdr.GetProperty("stop_lsn").GetString());
    }

    [Fact]
    public async Task Full_Backup_Concurrent_Insert_Stable()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.SetModeFullAsync();
        await conn.ExecAsync(
            "CREATE TABLE concur(id int PRIMARY KEY, v text);" +
            "INSERT INTO concur SELECT g, 'r' || g FROM generate_series(1, 1000) g;");

        var dbName = conn.Database!;
        var path = Helpers.BackupPath("full");

        var builder = new NpgsqlConnectionStringBuilder(_pg.ConnectionString) { Database = dbName };
        var inserter = new NpgsqlConnection(builder.ConnectionString);
        await inserter.OpenAsync();

        var backupTask = conn.BackupFullAsync(path);

        try
        {
            for (int i = 0; i < 20 && !backupTask.IsCompleted; i++)
            {
                try
                {
                    await using var cmd = inserter.CreateCommand();
                    cmd.CommandText =
                        $"INSERT INTO concur VALUES (1000 + {i}, 'cc{i}')";
                    cmd.CommandTimeout = 5;
                    await cmd.ExecuteNonQueryAsync();
                }
                catch { /* tolerate */ }
                await Task.Delay(50);
            }
        }
        finally
        {
            await inserter.CloseAsync();
        }

        await backupTask;

        await using var verifyCmd = conn.CreateCommand();
        verifyCmd.CommandText = "SELECT is_valid, detail FROM dbbackup.pg_dbbackup_verify(@path)";
        verifyCmd.Parameters.AddWithValue("path", path);
        await using var rdr = await verifyCmd.ExecuteReaderAsync();
        Assert.True(await rdr.ReadAsync());
        Assert.True(rdr.GetBoolean(0), rdr.IsDBNull(1) ? null : rdr.GetString(1));
    }

    [Fact]
    public async Task Full_Backup_Drops_Replication_Slot_On_Success()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.SetModeFullAsync();

        var path = Helpers.BackupPath("full");
        await conn.BackupFullAsync(path);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT count(*) FROM pg_replication_slots WHERE slot_name LIKE '_pg_dbbackup_%'";
        var n = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(0L, n);
    }

    [Fact]
    public async Task Full_Backup_Drops_Replication_Slot_On_Error()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.SetModeFullAsync();
        var dbName = conn.Database!;

        var badPath = "/nonexistent/dir/xxx.bak";
        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbbackup(@db, @path, type := 'full', compress := false)";
            cmd.Parameters.AddWithValue("db", dbName);
            cmd.Parameters.AddWithValue("path", badPath);
            await cmd.ExecuteNonQueryAsync();
        });

        await using var admin = _pg.CreateConnection();
        await admin.OpenAsync();
        await using var cmd2 = admin.CreateCommand();
        cmd2.CommandText =
            "SELECT count(*) FROM pg_replication_slots WHERE slot_name LIKE '_pg_dbbackup_%'";
        var n = (long)(await cmd2.ExecuteScalarAsync())!;
        Assert.Equal(0L, n);
    }
}
