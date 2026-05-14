using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// A restore chain with a missing link in the middle (FULL, [DIFF1
/// missing], LOG) must fail with an actionable error naming the gap.
/// Silently skipping the missing DIFF would replay LOG on top of FULL,
/// producing inconsistent state.
/// </summary>
public sealed class ChainGapDiffMissingTests
{
    private readonly PgContainerFixture _pg;

    public ChainGapDiffMissingTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Restore_Chain_With_Missing_Middle_Errors_Cleanly()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE chain_t(id int PRIMARY KEY, v text);" +
            "INSERT INTO chain_t VALUES (1, 'full');");

        var full = Helpers.BackupPath("chain_f");
        await src.BackupFullAsync(full, compress: false);

        await src.ExecAsync("INSERT INTO chain_t VALUES (2, 'diff');");
        var diff = Helpers.BackupPath("chain_d");
        await src.BackupDiffAsync(diff, full, compress: false);

        await src.ExecAsync("INSERT INTO chain_t VALUES (3, 'log');");
        var log = Helpers.BackupPath("chain_l");
        await src.BackupLogAsync(log, diff, compress: false);
        await src.CloseAsync();

        // Skip the DIFF in the restore call — pass only FULL + LOG.
        var target = "chgap_" + Guid.NewGuid().ToString("N")[..8];
        Exception? err = null;
        try
        {
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
            cmd.Parameters.AddWithValue("files", new[] { full, log });
            cmd.Parameters.AddWithValue("tgt", target);
            cmd.CommandTimeout = 60;
            await cmd.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
        }
        catch (PostgresException ex) { err = ex; }

        try
        {
            Assert.NotNull(err);
            var msg = ((PostgresException)err!).MessageText.ToLowerInvariant();
            Assert.True(
                msg.Contains("chain") || msg.Contains("gap") ||
                msg.Contains("missing") || msg.Contains("base") ||
                msg.Contains("lsn") || msg.Contains("link") ||
                msg.Contains("expect") || msg.Contains("diff") ||
                msg.Contains("predecessor") || msg.Contains("ancestor"),
                $"Chain-gap error must name the discontinuity: " +
                $"{((PostgresException)err).MessageText}");
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
