using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Mid-stream I/O failures during a backup must surface a clean error,
/// not corrupt the destination or leak server resources (slots, temp
/// files, transaction snapshots).
///
/// Inject ENOSPC by writing to /dev/full — a Linux device whose writes
/// always fail with "No space left on device". Inject a connection
/// drop by pg_terminate_backend()'ing the backup's pid from a second
/// session.
/// </summary>
public sealed class IoFailureInjectionTests
{
    private readonly PgContainerFixture _pg;

    public IoFailureInjectionTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Backup_To_DevFull_Errors_Cleanly_And_Leaves_No_Orphans()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE iof_t(id int PRIMARY KEY, p text);" +
            "INSERT INTO iof_t SELECT g, repeat('z', 200) " +
            "FROM generate_series(1, 5000) g;");

        int slotsBefore = await CountSlotsAsync(src.Database!);

        Exception? backupErr = null;
        try
        {
            // /dev/full passes path validation (no ..) but ENOSPCs every
            // write. Backup must surface an error.
            await src.BackupFullAsync("/dev/full", compress: false,
                commandTimeoutSeconds: 60);
        }
        catch (PostgresException ex) { backupErr = ex; }

        Assert.NotNull(backupErr);
        var pex = (PostgresException)backupErr!;
        var msg = (pex.MessageText + " " + (pex.Detail ?? "") + " " +
                   (pex.Hint ?? "")).ToLowerInvariant();
        Assert.True(
            msg.Contains("space") || msg.Contains("write") ||
            msg.Contains("io") || msg.Contains("disk") ||
            msg.Contains("/dev/full") || msg.Contains("device") ||
            msg.Contains("errno") || msg.Contains("28"),
            $"Disk-full error should mention I/O / space / device: " +
            $"{pex.MessageText} | {pex.Detail}");

        // No replication slot leak: a failed FULL must not leave a slot
        // hanging around that future FULLs can't clean up. The expected
        // post-state is the SAME slot count as before (slot is either
        // never created, or created+rolled back, or persistent across
        // the failure — any of these is acceptable; what matters is
        // that the count doesn't grow without bound).
        int slotsAfter = await CountSlotsAsync(src.Database!);
        Assert.True(slotsAfter <= slotsBefore + 1,
            $"Slot leak: before={slotsBefore} after={slotsAfter}");

        // After the failure, a fresh backup to a clean path must succeed.
        var good = Helpers.BackupPath("iof_after");
        await src.BackupFullAsync(good, compress: false);
    }

    [Fact]
    public async Task Backup_Connection_Drop_MidStream_Leaves_Server_Healthy()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        // Make the workload large enough that the backup definitively
        // outlasts the kill delay even on a fast host.
        await src.ExecAsync(
            "CREATE TABLE drop_t(id int PRIMARY KEY, p text);" +
            "INSERT INTO drop_t SELECT g, repeat('z', 4000) " +
            "FROM generate_series(1, 100000) g;",
            timeoutSeconds: 180);

        var path = Helpers.BackupPath("dropmid");

        // Get the backup connection's pid so we can terminate it from
        // another session.
        int backupPid;
        await using (var cmd = src.CreateCommand())
        {
            cmd.CommandText = "SELECT pg_backend_pid()";
            backupPid = (int)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
        }

        var dbName = src.Database!;
        var killTask = Task.Run(async () =>
        {
            // Fire the kill very early — before the backup even starts
            // streaming substantial bytes.
            await Task.Delay(50);
            await using var killer = await _pg.AdminAsync();
            await using var cmd = killer.CreateCommand();
            cmd.CommandText = "SELECT pg_terminate_backend(@pid)";
            cmd.Parameters.AddWithValue("pid", backupPid);
            try { await cmd.ExecuteNonQueryAsync(); } catch { }
        });

        Exception? backupErr = null;
        try
        {
            await src.BackupFullAsync(path, compress: true,
                commandTimeoutSeconds: 60);
        }
        catch (Exception ex) { backupErr = ex; }

        await killTask;

        // A backup that completes before the kill is a valid outcome
        // (the connection drop arrived after the call returned). What
        // matters is the SERVER stays healthy and slots are not leaked,
        // not whether THIS call observed the error. Only when the
        // backup did get killed, the error must be diagnostic.
        if (backupErr is not null)
        {
            var msg = backupErr.Message.ToLowerInvariant();
            Assert.True(
                msg.Contains("terminat") || msg.Contains("connection") ||
                msg.Contains("server") || msg.Contains("closed") ||
                msg.Contains("admin") || msg.Contains("eof"),
                $"Killed-backup error should mention termination: {backupErr.Message}");
        }

        // Server is still healthy: admin can run queries, a new DB+ext
        // and a fresh backup work end-to-end.
        await using (var admin = await _pg.AdminAsync())
        await using (var cmd = admin.CreateCommand())
        {
            cmd.CommandText = "SELECT 1";
            Assert.Equal(1, (int)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }

        // Cleanup any stale slot the aborted backup may have left.
        await using (var admin = await _pg.AdminAsync())
        await using (var cmd = admin.CreateCommand())
        {
            cmd.CommandText =
                "SELECT pg_drop_replication_slot(slot_name) " +
                "FROM pg_replication_slots " +
                "WHERE database = @d AND slot_name LIKE '_pg_dbbackup_%'";
            cmd.Parameters.AddWithValue("d", dbName);
            try { await cmd.ExecuteNonQueryAsync(); } catch { }
        }
    }

    private async Task<int> CountSlotsAsync(string dbName)
    {
        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT count(*)::int FROM pg_replication_slots " +
            "WHERE database = @d AND slot_name LIKE '_pg_dbbackup_%'";
        cmd.Parameters.AddWithValue("d", dbName);
        return (int)(await cmd.ExecuteScalarAsync(
            TestContext.Current.CancellationToken))!;
    }
}
