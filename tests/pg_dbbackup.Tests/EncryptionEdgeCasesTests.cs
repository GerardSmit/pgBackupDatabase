using Npgsql;
using NpgsqlTypes;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// AES-256-GCM password handling: very long password, unicode password,
/// password with embedded whitespace + punctuation, and an unencrypted
/// backup restored with a password argument (should be ignored).
/// </summary>
public sealed class EncryptionEdgeCasesTests
{
    private readonly PgContainerFixture _pg;

    public EncryptionEdgeCasesTests(PgContainerFixture pg) => _pg = pg;

    [Theory]
    [InlineData("simple")]
    [InlineData("with spaces and punctuation!?")]
    [InlineData("unicode ❄️ снеговик ☃ 雪人")]
    public async Task Password_RoundTrip_With_Edge_Strings(string password)
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY, v text);" +
            "INSERT INTO t VALUES (1, 'enc');");

        var path = Helpers.BackupPath("enc_edge");
        await src.BackupFullAsync(path, compress: true, password: password);
        await src.CloseAsync();

        var target = "enc_edge_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, path, password);
            await using var r = await _pg.ConnectToAsync(target);
            var n = (long)(await ExecScalarAsync(r,
                "SELECT count(*) FROM t"))!;
            Assert.Equal(1L, n);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task VeryLong_Password_RoundTrips()
    {
        var pw = new string('A', 4096);
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY);" +
            "INSERT INTO t VALUES (1);");

        var path = Helpers.BackupPath("enc_long");
        await src.BackupFullAsync(path, compress: true, password: pw);
        await src.CloseAsync();

        var target = "enc_long_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, path, pw);
            await using var r = await _pg.ConnectToAsync(target);
            var n = (long)(await ExecScalarAsync(r, "SELECT count(*) FROM t"))!;
            Assert.Equal(1L, n);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Plain_Backup_Ignores_Restore_Password_Argument()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY);" +
            "INSERT INTO t VALUES (42);");

        var path = Helpers.BackupPath("enc_plain");
        await src.BackupFullAsync(path, compress: false, password: null);
        await src.CloseAsync();

        var target = "enc_plain_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, path, "ignored-password");
            await using var r = await _pg.ConnectToAsync(target);
            var v = (int)(await ExecScalarAsync(r, "SELECT id FROM t"))!;
            Assert.Equal(42, v);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    private async Task RestoreAsync(string target, string path, string? password)
    {
        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbrestore(ARRAY[@p]::text[], " +
            "  target_db := @tgt, password := @pw)";
        cmd.Parameters.AddWithValue("p", path);
        cmd.Parameters.AddWithValue("tgt", target);
        cmd.Parameters.Add(new NpgsqlParameter("pw", NpgsqlDbType.Text)
        {
            Value = (object?)password ?? DBNull.Value,
        });
        cmd.CommandTimeout = 180;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        NpgsqlConnection.ClearAllPools();
    }

    private static async Task<object?> ExecScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        return await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }
}
