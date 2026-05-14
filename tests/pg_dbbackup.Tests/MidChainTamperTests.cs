using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Tamper detection on chain links beyond the FULL file: a flipped byte
/// in a DIFF or LOG payload must be rejected, not silently replayed.
/// </summary>
public sealed class MidChainTamperTests
{
    private readonly PgContainerFixture _pg;

    public MidChainTamperTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task BitFlip_In_Differential_Rejected_On_Restore()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY, v text);" +
            "INSERT INTO t SELECT g, 'x' || g FROM generate_series(1, 100) g;");

        var full = Helpers.BackupPath("mc_full");
        await src.BackupFullAsync(full, compress: false);

        await src.ExecAsync(
            "INSERT INTO t SELECT g, 'y' || g FROM generate_series(101, 200) g;");

        var diff = Helpers.BackupPath("mc_diff");
        await src.BackupDiffAsync(diff, basePath: full, compress: false);
        await src.CloseAsync();

        // Flip a byte at offset 1024 inside the DIFF file (past header).
        await _pg.ShellAsync(
            $"python3 -c \"f=open('{diff}','r+b'); f.seek(1024); " +
            $"b=f.read(1); f.seek(1024); f.write(bytes([b[0] ^ 0xFF])); f.close()\"");

        var target = "mc_diff_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(ARRAY[@full,@diff]::text[], " +
                "  target_db := @tgt)";
            cmd.Parameters.AddWithValue("full", full);
            cmd.Parameters.AddWithValue("diff", diff);
            cmd.Parameters.AddWithValue("tgt", target);
            cmd.CommandTimeout = 120;
            var ex = await Assert.ThrowsAsync<PostgresException>(
                () => cmd.ExecuteNonQueryAsync(
                    TestContext.Current.CancellationToken));
            Assert.NotNull(ex.SqlState);
        }
        finally { try { await _pg.DropDbAsync(target); } catch { } }
    }

    [Fact]
    public async Task Verify_Reports_Invalid_For_Tampered_Log_File()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY, v text);" +
            "INSERT INTO t VALUES (1,'a');");

        var full = Helpers.BackupPath("mc_log_full");
        await src.BackupFullAsync(full, compress: false);

        await src.ExecAsync(
            "INSERT INTO t SELECT g, 'b' || g FROM generate_series(2, 50) g;");

        var log = Helpers.BackupPath("mc_log");
        await src.BackupLogAsync(log, basePath: full, compress: false);
        await src.CloseAsync();

        await _pg.ShellAsync(
            $"python3 -c \"f=open('{log}','r+b'); f.seek(512); " +
            $"b=f.read(1); f.seek(512); f.write(bytes([b[0] ^ 0xAA])); f.close()\"");

        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT is_valid, detail FROM dbbackup.pg_dbbackup_verify(@p)";
        cmd.Parameters.AddWithValue("p", log);
        await using var rdr = await cmd.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
        Assert.False(rdr.GetBoolean(0));
        Assert.False(string.IsNullOrEmpty(rdr.GetString(1)));
    }
}
