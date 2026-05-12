using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class FullBackupTests
{
    private readonly PgContainerFixture _pg;

    public FullBackupTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Full_Backup_Creates_Bak_With_Schema_And_Data_Sections()
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

        Assert.Contains(sections, s => s.sectionType == Helpers.SectionSchema);
        Assert.Contains(sections, s => s.sectionType == Helpers.SectionData);
        Assert.DoesNotContain(sections, s => s.sectionType == Helpers.SectionLogicalStream);
        Assert.DoesNotContain(sections, s => s.sectionType == Helpers.SectionWalSegments);
    }

    [Fact]
    public async Task Full_Backup_Captures_Logical_Table_Data()
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

        Assert.Contains("public.rels", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith("base/"));
    }

    [Fact]
    public async Task Full_Backup_Does_Not_Capture_Global_Cluster_State()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.SetModeFullAsync();

        var path = Helpers.BackupPath("full");
        await conn.BackupFullAsync(path);

        var bytes = await _pg.ReadContainerFileAsync(path);
        var dataSection = Helpers.DecodeSections(bytes)
            .First(s => s.sectionType == Helpers.SectionData).data;
        var paths = Helpers.DecodeDataEntryPaths(dataSection);

        Assert.DoesNotContain(paths, p => p.StartsWith("global/"));
        Assert.DoesNotContain(paths, p => p.StartsWith("pg_xact/"));
        Assert.DoesNotContain(paths, p => p.StartsWith("pg_multixact/"));
        Assert.DoesNotContain(paths, p => p.StartsWith("pg_subtrans/"));
        Assert.DoesNotContain(paths, p => p.StartsWith("pg_commit_ts/"));
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
    public async Task Full_Backup_Creates_Logical_Chain_Slot_On_Success()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.SetModeFullAsync();

        var path = Helpers.BackupPath("full");
        await conn.BackupFullAsync(path);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT failover FROM pg_replication_slots " +
            "WHERE slot_name = '_pg_dbbackup_' || " +
            "(SELECT oid::text FROM pg_database WHERE datname = current_database()) " +
            "AND plugin = 'pg_dbbackup'";
        var failover = await cmd.ExecuteScalarAsync();
        Assert.Equal(true, failover);
    }

    [Fact]
    public async Task Full_Backup_Failover_Status_Reports_Primary_Chain_Slot()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.SetModeFullAsync();

        var path = Helpers.BackupPath("full");
        await conn.BackupFullAsync(path);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT slot_exists, failover, synced, temporary, standby_ready " +
            "FROM dbbackup.pg_dbbackup_failover_slot_status(current_database())";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
        Assert.False(reader.GetBoolean(3));
        Assert.False(reader.GetBoolean(4));
    }

    [Fact]
    public async Task Full_Log_Backup_Rejects_NonFailover_Chain_Slot()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.SetModeFullAsync();
        await conn.ExecAsync("CREATE TABLE t(id int PRIMARY KEY, v text); INSERT INTO t VALUES (1, 'base');");

        var fullPath = Helpers.BackupPath("full");
        var logPath = Helpers.BackupPath("log");
        await conn.BackupFullAsync(fullPath);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT '_pg_dbbackup_' || " +
                "(SELECT oid::text FROM pg_database WHERE datname = current_database())";
            var slotName = (string)(await cmd.ExecuteScalarAsync())!;

            cmd.Parameters.Clear();
            cmd.CommandText = "SELECT pg_drop_replication_slot(@slot::name)";
            cmd.Parameters.AddWithValue("slot", slotName);
            await cmd.ExecuteNonQueryAsync();

            cmd.Parameters.Clear();
            cmd.CommandText =
                "SELECT pg_create_logical_replication_slot(@slot::name, 'pg_dbbackup'::name, false, false, false)";
            cmd.Parameters.AddWithValue("slot", slotName);
            await cmd.ExecuteNonQueryAsync();
        }

        await conn.ExecAsync("INSERT INTO t VALUES (2, 'after');");

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            await conn.BackupLogAsync(logPath, fullPath));
        Assert.Contains("not failover-enabled", ex.MessageText);
    }

    [Fact]
    public async Task Full_Backup_Drops_Logical_Chain_Slot_On_Error()
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
            "SELECT count(*) FROM pg_replication_slots " +
            "WHERE slot_name = '_pg_dbbackup_' || " +
            "(SELECT oid::text FROM pg_database WHERE datname = @db)";
        cmd2.Parameters.AddWithValue("db", dbName);
        var n = (long)(await cmd2.ExecuteScalarAsync())!;
        Assert.Equal(0L, n);
    }
}
