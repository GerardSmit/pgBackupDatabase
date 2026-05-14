using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// UNLOGGED tables cannot participate in a logical backup because their
/// contents are not WAL-logged. The backup must refuse and return an
/// actionable error that names the offending table.
/// </summary>
public sealed class UnloggedTableErrorTests
{
    private readonly PgContainerFixture _pg;

    public UnloggedTableErrorTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Backup_Of_Unlogged_Table_Surfaces_Named_Error()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE UNLOGGED TABLE ulog_target(id int PRIMARY KEY);");

        var full = Helpers.BackupPath("ulog");
        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => src.BackupFullAsync(full, compress: false));

        // The message should reference unlogged status or the table name.
        var msg = ex.MessageText + " " + (ex.Detail ?? "") + " " +
                  (ex.Hint ?? "");
        Assert.True(
            msg.Contains("ulog_target", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("unlogged", StringComparison.OrdinalIgnoreCase),
            $"Expected error to mention table name or unlogged: {msg}");
    }
}
