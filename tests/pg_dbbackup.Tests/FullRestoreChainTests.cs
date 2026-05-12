using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class FullRestoreChainTests
{
    private readonly PgContainerFixture _pg;

    public FullRestoreChainTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task FullRestore_Accepts_Full_Plus_Diff()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY, v text);" +
            "INSERT INTO t VALUES (1,'one');");

        var fullPath = Helpers.BackupPath("chain");
        await src.BackupFullAsync(fullPath);

        await src.ExecAsync("INSERT INTO t VALUES (2,'two');");

        var diffPath = Helpers.BackupPath("chain");
        await src.BackupDiffAsync(diffPath, fullPath);
        await src.CloseAsync();

        var target = "chain_full_diff_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(@db, ARRAY[@p1, @p2]::text[], target_db := @tgt)";
            cmd.Parameters.AddWithValue("db", "ignored");
            cmd.Parameters.AddWithValue("p1", fullPath);
            cmd.Parameters.AddWithValue("p2", diffPath);
            cmd.Parameters.AddWithValue("tgt", target);
            cmd.CommandTimeout = 120;

            // Should succeed without errors at the chain/file level.
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task FullDiff_Rejects_Stale_Base_After_First_Diff_Moves_Slot()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY, v text);" +
            "INSERT INTO t VALUES (1,'one');");

        var fullPath = Helpers.BackupPath("chain");
        await src.BackupFullAsync(fullPath);

        await src.ExecAsync("INSERT INTO t VALUES (2,'two');");
        var diff1 = Helpers.BackupPath("chain");
        await src.BackupDiffAsync(diff1, fullPath);

        await src.ExecAsync("INSERT INTO t VALUES (3,'three');");
        var diff2 = Helpers.BackupPath("chain");
        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            await src.BackupDiffAsync(diff2, fullPath));
        Assert.Contains("previous backup does not match the active logical PITR chain",
            ex.MessageText);
    }

    [Fact]
    public async Task FullRestore_Rejects_Lsn_Gap()
    {
        // Two independent FULL backups → second one's "base" is unrelated.
        await using var srcA = await _pg.CreateFreshDbWithExtensionAsync();
        await srcA.SetModeFullAsync();
        await srcA.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY); INSERT INTO t VALUES (1);");

        var fullA = Helpers.BackupPath("chain");
        await srcA.BackupFullAsync(fullA);

        await using var srcB = await _pg.CreateFreshDbWithExtensionAsync();
        await srcB.SetModeFullAsync();
        await srcB.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY); INSERT INTO t VALUES (1);");

        // Forge a DIFF based on srcB's FULL but using srcA's db_name → trips db_name guard.
        // To genuinely hit LSN-gap rather than db_name mismatch, build a DIFF on B
        // and try to chain it after FULL on A.  validate_chain rejects mismatched db_name
        // first; we want the LSN error. Use the same db_name by using one DB:
        await srcB.CloseAsync();

        // Create unrelated FULL of srcA and DIFF of srcA, then mutate file order.
        await srcA.ExecAsync("INSERT INTO t VALUES (2);");
        var fullA2 = Helpers.BackupPath("chain");
        await srcA.BackupFullAsync(fullA2);

        var diff2 = Helpers.BackupPath("chain");
        await srcA.BackupDiffAsync(diff2, fullA2);
        await srcA.CloseAsync();

        var target = "chain_gap_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            // FULL(A) + DIFF(based on A2). DIFF.base_backup_lsn = A2.stop_lsn ≠ A.stop_lsn.
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(@db, ARRAY[@p1, @p2]::text[], target_db := @tgt)";
            cmd.Parameters.AddWithValue("db", "ignored");
            cmd.Parameters.AddWithValue("p1", fullA);
            cmd.Parameters.AddWithValue("p2", diff2);
            cmd.Parameters.AddWithValue("tgt", target);

            var ex = await Assert.ThrowsAsync<PostgresException>(
                () => cmd.ExecuteNonQueryAsync());
            Assert.Contains("LSN gap", ex.MessageText,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
