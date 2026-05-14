using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Multi-megabyte column round-trip through both DATA section (FULL) and
/// logical decoding (LOG). Exercises TOAST detoasting on backup and
/// reconstruction on restore.
/// </summary>
public sealed class ToastWideRowTests
{
    private readonly PgContainerFixture _pg;

    public ToastWideRowTests(PgContainerFixture pg) => _pg = pg;

    private const int WideMb = 5;

    [Fact]
    public async Task Toast_Wide_Row_FullRestore_Preserves_Bytes()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE wide(id int PRIMARY KEY, blob bytea NOT NULL, body text NOT NULL);",
            timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO wide(id, blob, body) " +
            $"VALUES (1, " +
            $"  decode(repeat('a1b2c3d4', {WideMb} * 1024 * 128), 'hex')," +
            $"  repeat('x', {WideMb} * 1024 * 1024));",
            timeoutSeconds: 120);

        // Capture source digests for comparison.
        var srcDigest = await ScalarStringAsync(src,
            "SELECT encode(sha256(blob), 'hex') || ':' || " +
            "       encode(sha256(body::bytea), 'hex') || ':' || " +
            "       octet_length(blob) || ':' || octet_length(body) " +
            "FROM wide WHERE id = 1");

        var full = Helpers.BackupPath("toast_full");
        await src.BackupFullAsync(full, compress: true, commandTimeoutSeconds: 600);
        await src.CloseAsync();

        var target = "toast_full_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, new[] { full });
            await using var r = await _pg.ConnectToAsync(target);
            var restored = await ScalarStringAsync(r,
                "SELECT encode(sha256(blob), 'hex') || ':' || " +
                "       encode(sha256(body::bytea), 'hex') || ':' || " +
                "       octet_length(blob) || ':' || octet_length(body) " +
                "FROM wide WHERE id = 1");
            Assert.Equal(srcDigest, restored);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Toast_Wide_Row_LogReplay_Preserves_Bytes()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE wide(id int PRIMARY KEY, blob bytea NOT NULL);",
            timeoutSeconds: 60);

        var full = Helpers.BackupPath("toast_full");
        await src.BackupFullAsync(full, compress: true, commandTimeoutSeconds: 600);

        // Insert a wide row after FULL — only logical decoding can carry it.
        await src.ExecAsync(
            "INSERT INTO wide(id, blob) " +
            $"VALUES (1, decode(repeat('beadface', {WideMb} * 1024 * 128), 'hex'));",
            timeoutSeconds: 300);

        var srcDigest = await ScalarStringAsync(src,
            "SELECT encode(sha256(blob), 'hex') || ':' || octet_length(blob) " +
            "FROM wide WHERE id = 1");

        var log = Helpers.BackupPath("toast_log");
        await src.BackupLogAsync(log, full, compress: true, commandTimeoutSeconds: 600);
        await src.CloseAsync();

        var target = "toast_log_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, new[] { full, log });
            await using var r = await _pg.ConnectToAsync(target);
            var restored = await ScalarStringAsync(r,
                "SELECT encode(sha256(blob), 'hex') || ':' || octet_length(blob) " +
                "FROM wide WHERE id = 1");
            Assert.Equal(srcDigest, restored);
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
        cmd.CommandTimeout = 600;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        NpgsqlConnection.ClearAllPools();
    }

    private static async Task<string> ScalarStringAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;
        return (string)(await cmd.ExecuteScalarAsync(
            TestContext.Current.CancellationToken))!;
    }
}
