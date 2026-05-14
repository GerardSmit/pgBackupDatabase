using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Large object (pg_largeobject) coverage. SIMPLE-mode FULL round-trips
/// LO content, FULL-mode chain captures lo_create / lo_put / lo_unlink
/// through the dbbackup.large_object_log journal.
/// </summary>
public sealed class LargeObjectPitrTests
{
    private readonly PgContainerFixture _pg;

    public LargeObjectPitrTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task LargeObject_Simple_FullRestore_RoundTrips_Content()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();

        // Create LO with known content, ~16 KB so spans multiple 2 KB pages.
        await src.ExecAsync(
            "CREATE TABLE lo_ref(id int PRIMARY KEY, loid oid NOT NULL);" +
            "SELECT lo_create(99001);" +
            "SELECT lo_put(99001, 0, decode(repeat('deadbeef', 2048), 'hex'));" +
            "INSERT INTO lo_ref VALUES (1, 99001);");

        var full = Helpers.BackupPath("lo_simple");
        await src.BackupFullAsync(full, compress: true, commandTimeoutSeconds: 180);
        await src.CloseAsync();

        var target = "lo_simple_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, new[] { full });
            await using var r = await _pg.ConnectToAsync(target);

            var loid = await ScalarLongAsync(r,
                "SELECT loid::bigint FROM lo_ref WHERE id = 1");
            Assert.Equal(99001L, loid);

            var size = await ScalarLongAsync(r,
                "SELECT octet_length(lo_get(99001))");
            Assert.Equal(8L * 1024, size);

            var head = (string)(await ExecScalarAsync(r,
                "SELECT encode(substring(lo_get(99001) FROM 1 FOR 8), 'hex')"))!;
            Assert.Equal("deadbeefdeadbeef", head);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task LargeObject_FullModeChain_Replays_Create_Put_Unlink()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();

        await src.ExecAsync(
            "CREATE TABLE lo_ref(id int PRIMARY KEY, loid oid NOT NULL);" +
            "SELECT lo_create(88001);" +
            "SELECT lo_put(88001, 0, decode(repeat('cafe', 1024), 'hex'));" +
            "INSERT INTO lo_ref VALUES (1, 88001);");

        var full = Helpers.BackupPath("lo_full");
        await src.BackupFullAsync(full, compress: true, commandTimeoutSeconds: 180);

        // After FULL: create new LO, mutate existing, unlink one.
        await src.ExecAsync(
            "SELECT lo_create(88002);" +
            "SELECT lo_put(88002, 0, decode(repeat('feed', 512), 'hex'));" +
            "INSERT INTO lo_ref VALUES (2, 88002);" +
            "SELECT lo_put(88001, 0, decode(repeat('beef', 1024), 'hex'));");

        var log1 = Helpers.BackupPath("lo_full_log1");
        await src.BackupLogAsync(log1, full, compress: true, commandTimeoutSeconds: 180);

        await src.ExecAsync(
            "SELECT lo_unlink(88001);" +
            "DELETE FROM lo_ref WHERE id = 1;");

        var log2 = Helpers.BackupPath("lo_full_log2");
        await src.BackupLogAsync(log2, log1, compress: true, commandTimeoutSeconds: 180);
        await src.CloseAsync();

        var target = "lo_full_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, new[] { full, log1, log2 });
            await using var r = await _pg.ConnectToAsync(target);

            // 88001 was unlinked.
            var exists88001 = (long)(await ExecScalarAsync(r,
                "SELECT count(*) FROM pg_largeobject_metadata WHERE oid = 88001"))!;
            Assert.Equal(0L, exists88001);

            // 88002 must exist with feed pattern.
            // 'feed' = 2 hex bytes × 512 repeats = 1024 raw bytes.
            var size = await ScalarLongAsync(r,
                "SELECT octet_length(lo_get(88002))");
            Assert.Equal(1024L, size);
            var head = (string)(await ExecScalarAsync(r,
                "SELECT encode(substring(lo_get(88002) FROM 1 FOR 4), 'hex')"))!;
            Assert.Equal("feedfeed", head);

            var rows = await ScalarLongAsync(r, "SELECT count(*) FROM lo_ref");
            Assert.Equal(1L, rows);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    private async Task RestoreAsync(string target, string[] files)
    {
        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
        cmd.Parameters.AddWithValue("files", files);
        cmd.Parameters.AddWithValue("tgt", target);
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        NpgsqlConnection.ClearAllPools();
    }

    private static async Task<long> ScalarLongAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        var raw = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return Convert.ToInt64(raw);
    }

    private static async Task<object?> ExecScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        return await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }
}
