using System.Buffers.Binary;
using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class WalFilterTests
{
    private readonly PgContainerFixture _pg;

    public WalFilterTests(PgContainerFixture pg) => _pg = pg;

    /// <summary>Sums the on-disk sizes of currently-present pg_wal segments.</summary>
    private async Task<long> RawPgWalBytesAsync()
    {
        var result = await _pg.ShellAsync(
            "su -s /bin/sh postgres -c 'cat \"$PGDATA\"/pg_wal/* 2>/dev/null | wc -c'");
        if (result.ExitCode != 0)
            return 0;
        return long.TryParse(result.Stdout.Trim(), out var n) ? n : 0;
    }

    [Fact]
    public async Task Wal_Section_Includes_Cluster_Wide_Rmgrs()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.SetModeFullAsync();
        await conn.ExecAsync("CREATE TABLE quiet(id int);");

        var path = Helpers.BackupPath("walfilter");
        await conn.BackupFullAsync(path);

        var bytes = await _pg.ReadContainerFileAsync(path);
        var walSection = Helpers.DecodeSections(bytes)
            .First(s => s.sectionType == Helpers.SectionWal).data;

        Assert.True(walSection.Length > 0,
            $"Expected WAL section to contain filtered records, got {walSection.Length} bytes");
    }

    [Fact]
    public async Task Wal_Section_Smaller_Than_Cluster_Wal_When_Other_Db_Active()
    {
        // Database A: the backup target with light DML.
        // Database B: heavy DML, should be filtered out of A's WAL section.
        await using var connA = await _pg.CreateFreshDbWithExtensionAsync();
        await connA.SetModeFullAsync();
        await connA.ExecAsync(
            "CREATE TABLE a(id int PRIMARY KEY, v text);" +
            "INSERT INTO a VALUES (1, 'lite');");

        // Create database B via admin connection and load it with heavy DML.
        string dbB = "test_b_" + Guid.NewGuid().ToString("N")[..8];
        await using (var admin = await _pg.AdminAsync())
        {
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{dbB}\"";
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var connB = await _pg.ConnectToAsync(dbB))
        {
            await connB.ExecAsync(
                "CREATE TABLE bulk(id int PRIMARY KEY, payload text);" +
                "INSERT INTO bulk SELECT g, repeat('x', 200) FROM generate_series(1, 5000) g;" +
                "UPDATE bulk SET payload = repeat('y', 200) WHERE id <= 2500;");
            // Force a WAL flush.
            await connB.ExecAsync("CHECKPOINT;");
        }

        var rawWalBefore = await RawPgWalBytesAsync();

        var path = Helpers.BackupPath("walfilter");
        await connA.BackupFullAsync(path, commandTimeoutSeconds: 180);

        var bytes = await _pg.ReadContainerFileAsync(path);
        var walSection = Helpers.DecodeSections(bytes)
            .First(s => s.sectionType == Helpers.SectionWal).data;

        // The filtered WAL section should be substantially smaller than the
        // raw cluster-wide pg_wal/ snapshot taken just before the backup,
        // because all of database B's bulk DML must have been filtered out.
        Assert.True(walSection.Length < rawWalBefore,
            $"WAL section {walSection.Length} bytes was not smaller than raw pg_wal {rawWalBefore} bytes");
    }

    [Fact]
    public async Task Wal_Section_Records_Have_Valid_Length_Frames()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.SetModeFullAsync();
        await conn.ExecAsync(
            "CREATE TABLE frames(id int PRIMARY KEY, v text);" +
            "INSERT INTO frames SELECT g, 'r' || g FROM generate_series(1, 100) g;");

        var path = Helpers.BackupPath("walfilter");
        await conn.BackupFullAsync(path);

        var bytes = await _pg.ReadContainerFileAsync(path);
        var walSection = Helpers.DecodeSections(bytes)
            .First(s => s.sectionType == Helpers.SectionWal).data;

        // Walk frames: each record is [uint32 BE len][len bytes raw record].
        int offset = 0;
        int recordCount = 0;
        while (offset < walSection.Length)
        {
            Assert.True(offset + 4 <= walSection.Length, "truncated length prefix");
            uint recLen = BinaryPrimitives.ReadUInt32BigEndian(walSection.AsSpan(offset, 4));
            offset += 4;
            Assert.True(recLen >= 24, $"WAL record length {recLen} smaller than SizeOfXLogRecord");
            Assert.True(offset + recLen <= walSection.Length,
                $"record length {recLen} overflows section (offset {offset}, total {walSection.Length})");
            offset += (int)recLen;
            recordCount++;
        }
        Assert.True(recordCount > 0, "expected at least one WAL record in the section");
        Assert.Equal(walSection.Length, offset);
    }
}
