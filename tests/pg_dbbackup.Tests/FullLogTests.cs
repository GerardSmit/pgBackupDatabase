using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class FullLogTests
{
    private readonly PgContainerFixture _pg;

    public FullLogTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task FullLog_Bak_Has_No_Data_Section()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY); INSERT INTO t VALUES (1);");

        var fullPath = Helpers.BackupPath("fulllog");
        await src.BackupFullAsync(fullPath);

        await src.ExecAsync("INSERT INTO t VALUES (2),(3),(4);");

        var logPath = Helpers.BackupPath("fulllog");
        await src.BackupLogAsync(logPath, fullPath);

        var bytes = await _pg.ReadContainerFileAsync(logPath);
        var types = Helpers.ListSectionTypes(bytes);

        Assert.DoesNotContain(Helpers.SectionData, types);
        Assert.Contains(Helpers.SectionWal, types);
    }

    [Fact]
    public async Task FullLog_Header_Reports_Log_Type()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY); INSERT INTO t VALUES (1);");

        var fullPath = Helpers.BackupPath("fulllog");
        await src.BackupFullAsync(fullPath);

        await src.ExecAsync("INSERT INTO t VALUES (2);");

        var logPath = Helpers.BackupPath("fulllog");
        await src.BackupLogAsync(logPath, fullPath);

        var bytes = await _pg.ReadContainerFileAsync(logPath);
        var hdr = Helpers.ReadHeader(bytes);

        Assert.Equal("full", hdr.GetProperty("mode").GetString());
        Assert.Equal("log", hdr.GetProperty("type").GetString());
    }

    [Fact]
    public async Task FullLog_Lsn_Linkage()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY); INSERT INTO t VALUES (1);");

        var fullPath = Helpers.BackupPath("fulllog");
        await src.BackupFullAsync(fullPath);

        await src.ExecAsync("INSERT INTO t VALUES (2);");

        var logPath = Helpers.BackupPath("fulllog");
        await src.BackupLogAsync(logPath, fullPath);

        var fullBytes = await _pg.ReadContainerFileAsync(fullPath);
        var logBytes = await _pg.ReadContainerFileAsync(logPath);

        var fullHdr = Helpers.ReadHeader(fullBytes);
        var logHdr = Helpers.ReadHeader(logBytes);

        var fullStop = fullHdr.GetProperty("stop_lsn").GetString();
        var logBase = logHdr.GetProperty("base_backup_lsn").GetString();

        Assert.Equal(fullStop, logBase);
    }
}
