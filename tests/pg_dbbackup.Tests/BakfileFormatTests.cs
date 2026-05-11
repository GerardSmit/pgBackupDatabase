using System.Text;
using System.Text.Json;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class BakfileFormatTests
{
    private readonly PgContainerFixture _pg;

    public BakfileFormatTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Bak_Magic_Bytes()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        var path = Helpers.BackupPath();
        await conn.BackupFullAsync(path);

        var bytes = await _pg.ReadContainerFileAsync(path);
        Assert.True(bytes.Length >= 10, $"backup too small ({bytes.Length} bytes)");

        var head = Encoding.ASCII.GetString(bytes, 0, 5);
        var tail = Encoding.ASCII.GetString(bytes, bytes.Length - 5, 5);

        Assert.Equal("PGBAK", head);
        Assert.Equal("PGBAK", tail);
    }

    [Fact]
    public async Task Bak_Header_Json()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        var dbName = conn.Database!;
        var path = Helpers.BackupPath();
        await conn.BackupFullAsync(path);

        var bytes = await _pg.ReadContainerFileAsync(path);
        var hdr = Helpers.ReadHeader(bytes);

        Assert.Equal("simple", hdr.GetProperty("mode").GetString());
        Assert.Equal("full", hdr.GetProperty("type").GetString());
        Assert.Equal(dbName, hdr.GetProperty("db_name").GetString());
    }

    [Fact]
    public async Task Bak_Truncation_Detected()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        var path = Helpers.BackupPath();
        await conn.BackupFullAsync(path);

        var truncate = await _pg.ShellAsync(
            $"set -e; sz=$(stat -c %s {path}); newsz=$((sz - 100)); " +
            $"dd if={path} of={path}.trunc bs=1 count=$newsz status=none; mv {path}.trunc {path}");
        Assert.Equal(0, truncate.ExitCode);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT is_valid, detail FROM dbbackup.pg_dbbackup_verify(@path)";
        cmd.Parameters.AddWithValue("path", path);
        await using var rdr = await cmd.ExecuteReaderAsync();
        Assert.True(await rdr.ReadAsync());
        var isValid = rdr.GetBoolean(0);
        Assert.False(isValid);
    }

    [Fact]
    public async Task Bak_Contains_Data_Section_For_User_Tables()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.ExecAsync(
            "CREATE TABLE big(id int PRIMARY KEY, payload text);" +
            "INSERT INTO big SELECT g, repeat('x', 200) FROM generate_series(1, 200) g;");

        var path = Helpers.BackupPath();
        await conn.BackupFullAsync(path);

        var bytes = await _pg.ReadContainerFileAsync(path);
        Assert.True(bytes.Length > 1024, $"backup file unexpectedly small: {bytes.Length} bytes");
    }

    [Fact]
    public async Task Bak_Verify_Reports_Valid()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        var path = Helpers.BackupPath();
        await conn.BackupFullAsync(path);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT is_valid, detail FROM dbbackup.pg_dbbackup_verify(@path)";
        cmd.Parameters.AddWithValue("path", path);
        await using var rdr = await cmd.ExecuteReaderAsync();
        Assert.True(await rdr.ReadAsync());
        var isValid = rdr.GetBoolean(0);
        var detail = rdr.IsDBNull(1) ? null : rdr.GetString(1);
        Assert.True(isValid, $"verify failed: {detail}");
    }

    [Fact]
    public async Task Bak_Restore_Header_Reports_Simple_Full()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        var dbName = conn.Database!;
        var path = Helpers.BackupPath();
        await conn.BackupFullAsync(path);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT backup_type, mode, db_name " +
            "FROM dbbackup.pg_dbbackup_header(@path)";
        cmd.Parameters.AddWithValue("path", path);

        await using var rdr = await cmd.ExecuteReaderAsync();
        Assert.True(await rdr.ReadAsync());
        Assert.Equal("full", rdr.GetString(0));
        Assert.Equal("simple", rdr.GetString(1));
        Assert.Equal(dbName, rdr.GetString(2));
    }
}
