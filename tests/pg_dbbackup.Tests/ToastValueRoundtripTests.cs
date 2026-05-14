using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;
using System.Security.Cryptography;
using System.Text;

namespace PgDbBackup.Tests;

/// <summary>
/// Values larger than 2KB get TOASTed into a side relation. Logical
/// decoding emits them as the assembled text, but the backup must
/// correctly transport them through compression+framing without
/// truncation or splice errors.
/// </summary>
public sealed class ToastValueRoundtripTests
{
    private readonly PgContainerFixture _pg;

    public ToastValueRoundtripTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Toasted_Text_Values_Survive_Backup_Restore()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE toast_t(id int PRIMARY KEY, blob text NOT NULL);");

        // Three distinct payload sizes spanning TOAST thresholds:
        // 1KB (inline), 4KB (compressed inline), 200KB (external storage).
        var sizes = new[] { 1024, 4096, 200_000 };
        var hashes = new string[sizes.Length];
        for (var i = 0; i < sizes.Length; i++)
        {
            var data = MakePayload(sizes[i], seed: i);
            hashes[i] = Sha256Hex(data);
            await using var cmd = src.CreateCommand();
            cmd.CommandText = "INSERT INTO toast_t(id, blob) VALUES (@i, @b)";
            cmd.Parameters.AddWithValue("i", i);
            cmd.Parameters.AddWithValue("b", data);
            cmd.CommandTimeout = 60;
            await cmd.ExecuteNonQueryAsync();
        }

        var full = Helpers.BackupPath("toast");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "toast_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
            cmd.Parameters.AddWithValue("files", new[] { full });
            cmd.Parameters.AddWithValue("tgt", target);
            cmd.CommandTimeout = 300;
            await cmd.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
            NpgsqlConnection.ClearAllPools();

            await using var r = await _pg.ConnectToAsync(target);
            for (var i = 0; i < sizes.Length; i++)
            {
                await using var c = r.CreateCommand();
                c.CommandText =
                    "SELECT encode(sha256(blob::bytea), 'hex') FROM toast_t WHERE id = @i";
                c.Parameters.AddWithValue("i", i);
                var got = (string)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.Equal(hashes[i], got);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    private static string MakePayload(int len, int seed)
    {
        var sb = new StringBuilder(len);
        var rng = new Random(seed);
        const string alpha = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        for (var i = 0; i < len; i++)
            sb.Append(alpha[rng.Next(alpha.Length)]);
        return sb.ToString();
    }

    private static string Sha256Hex(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
