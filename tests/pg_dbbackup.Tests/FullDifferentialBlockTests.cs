using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class FullDifferentialLogicalTests
{
    private readonly PgContainerFixture _pg;

    public FullDifferentialLogicalTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task FullDiff_Stores_Logical_Stream_Not_Block_Data()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY, v text);" +
            "INSERT INTO t VALUES (1, 'one'), (2, 'two'), (3, 'three');");

        var fullPath = Helpers.BackupPath("fulldifflog");
        await src.BackupFullAsync(fullPath);

        await src.ExecAsync(
            "INSERT INTO t VALUES (4, 'four');" +
            "UPDATE t SET v = 'TWO' WHERE id = 2;" +
            "DELETE FROM t WHERE id = 3;");

        var diffPath = Helpers.BackupPath("fulldifflog");
        await src.BackupDiffAsync(diffPath, fullPath);

        var diffBytes = await _pg.ReadContainerFileAsync(diffPath);
        var types = Helpers.ListSectionTypes(diffBytes);

        Assert.DoesNotContain(Helpers.SectionData, types);
        Assert.Contains(Helpers.SectionLogicalStream, types);
        Assert.DoesNotContain(Helpers.SectionWalSegments, types);

        var logical = Helpers.DecodeSections(diffBytes)
            .First(s => s.sectionType == Helpers.SectionLogicalStream).data;
        var frames = Helpers.DecodeLogicalStream(logical);

        Assert.Contains(frames, f => f.Contains("INSERT INTO public.t"));
        Assert.Contains(frames, f => f.Contains("UPDATE public.t"));
        Assert.Contains(frames, f => f.Contains("DELETE FROM public.t"));
    }

    [Fact]
    public async Task FullDiff_Logical_Restore_Roundtrip()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE diff_roundtrip(id int PRIMARY KEY, v text);" +
            "INSERT INTO diff_roundtrip VALUES (1, 'orig1'), (2, 'orig2'), (3, 'orig3');");

        var fullPath = Helpers.BackupPath("fulldifflog");
        await src.BackupFullAsync(fullPath);

        await src.ExecAsync(
            "UPDATE diff_roundtrip SET v = 'changed' WHERE id = 2;" +
            "INSERT INTO diff_roundtrip VALUES (4, 'late');" +
            "DELETE FROM diff_roundtrip WHERE id = 1;");

        var diffPath = Helpers.BackupPath("fulldifflog");
        await src.BackupDiffAsync(diffPath, fullPath);
        await src.CloseAsync();

        var target = "blk_roundtrip_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using (var admin = await _pg.AdminAsync())
            await using (var cmd = admin.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbrestore(@db, ARRAY[@p1, @p2]::text[], target_db := @tgt)";
                cmd.Parameters.AddWithValue("db", "ignored");
                cmd.Parameters.AddWithValue("p1", fullPath);
                cmd.Parameters.AddWithValue("p2", diffPath);
                cmd.Parameters.AddWithValue("tgt", target);
                cmd.CommandTimeout = 120;
                await cmd.ExecuteNonQueryAsync();
            }

            await using var rconn = await _pg.ConnectToAsync(target);
            await using var q = rconn.CreateCommand();
            q.CommandText =
                "SELECT id, v FROM diff_roundtrip ORDER BY id";
            await using var rdr = await q.ExecuteReaderAsync();
            var vals = new List<(int id, string v)>();
            while (await rdr.ReadAsync())
                vals.Add((rdr.GetInt32(0), rdr.GetString(1)));

            Assert.Equal(new[] { (2, "changed"), (3, "orig3"), (4, "late") }, vals);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task FullDiff_Empty_Range_Is_Logical_And_Restorable()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE empty_diff(id int PRIMARY KEY, v text);" +
            "INSERT INTO empty_diff VALUES (1, 'stable');");

        var fullPath = Helpers.BackupPath("fulldifflog");
        await src.BackupFullAsync(fullPath);

        var diffPath = Helpers.BackupPath("fulldifflog");
        await src.BackupDiffAsync(diffPath, fullPath);
        await src.CloseAsync();

        var bytes = await _pg.ReadContainerFileAsync(diffPath);
        var hdr = Helpers.ReadHeader(bytes);
        Assert.Equal("differential", hdr.GetProperty("type").GetString());
        Assert.Contains(Helpers.SectionLogicalStream, Helpers.ListSectionTypes(bytes));

        var target = "empty_diff_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using (var admin = await _pg.AdminAsync())
            await using (var cmd = admin.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbrestore(@db, ARRAY[@p1, @p2]::text[], target_db := @tgt)";
                cmd.Parameters.AddWithValue("db", "ignored");
                cmd.Parameters.AddWithValue("p1", fullPath);
                cmd.Parameters.AddWithValue("p2", diffPath);
                cmd.Parameters.AddWithValue("tgt", target);
                cmd.CommandTimeout = 120;
                await cmd.ExecuteNonQueryAsync();
            }

            await using var rconn = await _pg.ConnectToAsync(target);
            await using var q = rconn.CreateCommand();
            q.CommandText = "SELECT v FROM empty_diff WHERE id = 1";
            Assert.Equal("stable", await q.ExecuteScalarAsync());
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
