using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class FullDifferentialTests
{
    private readonly PgContainerFixture _pg;

    public FullDifferentialTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task FullDiff_Bak_Smaller_Than_Full()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE big(id int PRIMARY KEY, payload text);" +
            "INSERT INTO big SELECT g, repeat('x', 200) FROM generate_series(1, 2000) g;" +
            "CHECKPOINT;");

        var fullPath = Helpers.BackupPath("fulldiff");
        await src.BackupFullAsync(fullPath);

        // Immediate diff with no DML between → expected to be much smaller
        var diffPath = Helpers.BackupPath("fulldiff");
        await src.BackupDiffAsync(diffPath, fullPath);

        var fullBytes = await _pg.ReadContainerFileAsync(fullPath);
        var diffBytes = await _pg.ReadContainerFileAsync(diffPath);

        Assert.True(diffBytes.Length < fullBytes.Length,
            $"DIFF ({diffBytes.Length}) not smaller than FULL ({fullBytes.Length})");
    }

    [Fact]
    public async Task FullDiff_Has_Differential_Type_In_Header()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY, v text);" +
            "INSERT INTO t SELECT g, 'r'||g FROM generate_series(1, 50) g;");

        var fullPath = Helpers.BackupPath("fulldiff");
        await src.BackupFullAsync(fullPath);

        await src.ExecAsync("INSERT INTO t VALUES (200,'late');");

        var diffPath = Helpers.BackupPath("fulldiff");
        await src.BackupDiffAsync(diffPath, fullPath);

        var bytes = await _pg.ReadContainerFileAsync(diffPath);
        var hdr = Helpers.ReadHeader(bytes);

        Assert.Equal("full", hdr.GetProperty("mode").GetString());
        Assert.Equal("differential", hdr.GetProperty("type").GetString());
        Assert.NotEqual("00000000/00000000",
            hdr.GetProperty("base_backup_lsn").GetString());
    }

    [Fact]
    public async Task FullDiff_Logical_Stream_Covers_Range()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY, v text);" +
            "INSERT INTO t SELECT g, 'r'||g FROM generate_series(1, 50) g;");

        var fullPath = Helpers.BackupPath("fulldiff");
        await src.BackupFullAsync(fullPath);

        await src.ExecAsync("INSERT INTO t VALUES (51,'late');");

        var diffPath = Helpers.BackupPath("fulldiff");
        await src.BackupDiffAsync(diffPath, fullPath);

        var bytes = await _pg.ReadContainerFileAsync(diffPath);
        var logicalSection = Helpers.WalkSections(bytes)
            .FirstOrDefault(s => s.sectionType == Helpers.SectionLogicalStream);

        Assert.True(logicalSection.sectionType == Helpers.SectionLogicalStream,
            "DIFF .bak missing LOGICAL_STREAM section");
        Assert.True(logicalSection.length > 0, "LOGICAL_STREAM section is empty");
    }
}
