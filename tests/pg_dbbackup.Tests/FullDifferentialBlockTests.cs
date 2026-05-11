using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class FullDifferentialBlockTests
{
    private readonly PgContainerFixture _pg;

    public FullDifferentialBlockTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task FullDiff_With_WalSummaries_Is_Smaller_Than_Whole_File()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();

        // Build a table large enough that the relation is much bigger than
        // one 8KB page. 10000 rows with ~200 byte payload comfortably crosses
        // many heap pages.
        await src.ExecAsync(
            "CREATE TABLE big(id int PRIMARY KEY, payload text);" +
            "INSERT INTO big SELECT g, repeat('x', 200) FROM generate_series(1, 10000) g;" +
            "CHECKPOINT;");

        long heapSize = await GetRelationFileSizeAsync(src, "big");
        Assert.True(heapSize > 16 * 8192,
            $"Sanity: expected heap file > 16 pages, got {heapSize}");

        var fullPath = Helpers.BackupPath("fulldiffblk");
        await src.BackupFullAsync(fullPath);

        // Update a single row → one (or two) heap blocks dirty. With WAL
        // summaries the DIFF should contain only those pages plus any
        // index/FSM updates.
        await src.ExecAsync("UPDATE big SET payload = 'tiny' WHERE id = 1;");
        await src.ExecAsync("CHECKPOINT;");

        var diffPath = Helpers.BackupPath("fulldiffblk");
        await src.BackupDiffAsync(diffPath, fullPath);

        var diffBytes = await _pg.ReadContainerFileAsync(diffPath);

        // The DIFF .bak should be far smaller than the whole heap file.
        // Aim for < 10% of the heap size as a generous upper bound.
        Assert.True(diffBytes.Length < heapSize / 10,
            $"DIFF ({diffBytes.Length}) not < heap_size/10 ({heapSize / 10}); " +
            $"WAL-summary block-level mode likely not active");

        // The DATA section should contain block-level entries (paths with ':').
        var dataSection = Helpers.WalkSections(diffBytes)
            .First(s => s.sectionType == Helpers.SectionData);
        var slice = new byte[dataSection.length];
        Buffer.BlockCopy(diffBytes, dataSection.dataOffset, slice, 0,
            (int)dataSection.length);
        var paths = Helpers.DecodeDataEntryPaths(slice);

        Assert.NotEmpty(paths);
        Assert.Contains(paths, p => p.Contains(":main:"));
    }

    [Fact]
    public async Task FullDiff_Block_Level_Restore_Roundtrip()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE blk(id int PRIMARY KEY, v text);" +
            "INSERT INTO blk SELECT g, 'orig'||g FROM generate_series(1, 2000) g;" +
            "CHECKPOINT;");

        var fullPath = Helpers.BackupPath("fulldiffblk");
        await src.BackupFullAsync(fullPath);

        // Mutate a handful of specific rows; the DIFF must capture them.
        await src.ExecAsync(
            "UPDATE blk SET v = 'changed' WHERE id IN (1, 500, 1500);" +
            "CHECKPOINT;");

        var diffPath = Helpers.BackupPath("fulldiffblk");
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
                "SELECT v FROM blk WHERE id IN (1, 500, 1500) ORDER BY id";
            await using var rdr = await q.ExecuteReaderAsync();
            var vals = new List<string>();
            while (await rdr.ReadAsync())
                vals.Add(rdr.GetString(0));

            Assert.Equal(new[] { "changed", "changed", "changed" }, vals);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task FullDiff_Wal_Summary_Disabled_Falls_Back_To_Mtime()
    {
        await using (var admin = await _pg.AdminAsync())
        await using (var cmd = admin.CreateCommand())
        {
            cmd.CommandText =
                "ALTER SYSTEM SET summarize_wal=off; SELECT pg_reload_conf();";
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            await using var src = await _pg.CreateFreshDbWithExtensionAsync();
            await src.SetModeFullAsync();
            await src.ExecAsync(
                "CREATE TABLE t(id int PRIMARY KEY, v text);" +
                "INSERT INTO t SELECT g, 'r'||g FROM generate_series(1, 100) g;" +
                "CHECKPOINT;");

            var fullPath = Helpers.BackupPath("fulldifffb");
            await src.BackupFullAsync(fullPath);

            await src.ExecAsync("INSERT INTO t VALUES (101, 'late');");

            var diffPath = Helpers.BackupPath("fulldifffb");
            // Should still succeed via mtime fallback.
            await src.BackupDiffAsync(diffPath, fullPath);

            var bytes = await _pg.ReadContainerFileAsync(diffPath);
            // Header sanity: still a differential.
            var hdr = Helpers.ReadHeader(bytes);
            Assert.Equal("differential", hdr.GetProperty("type").GetString());
        }
        finally
        {
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "ALTER SYSTEM SET summarize_wal=on; SELECT pg_reload_conf();";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task<long> GetRelationFileSizeAsync(
        NpgsqlConnection conn, string relname)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_relation_size(@r)";
        cmd.Parameters.AddWithValue("r", relname);
        var v = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(v);
    }
}
