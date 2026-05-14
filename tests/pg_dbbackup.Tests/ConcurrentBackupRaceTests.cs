using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Race-condition coverage: two backups against the same database fired
/// in parallel must both finish without producing an unrestorable .bak
/// or corrupting the chain endpoint. Either both succeed (FULLs from
/// independent backends) or one fails cleanly (LOG chain serialization).
/// </summary>
public sealed class ConcurrentBackupRaceTests
{
    private readonly PgContainerFixture _pg;

    public ConcurrentBackupRaceTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Two_Simultaneous_Full_Backups_Both_Restorable()
    {
        await using var setup = await _pg.CreateFreshDbWithExtensionAsync();
        var db = setup.Database!;
        await setup.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY, payload text);" +
            "INSERT INTO t SELECT g, md5(g::text) FROM generate_series(1, 5000) g;",
            timeoutSeconds: 60);
        await setup.CloseAsync();

        var p1 = Helpers.BackupPath("race_full_a");
        var p2 = Helpers.BackupPath("race_full_b");

        async Task BackupAsync(string path)
        {
            await using var c = await _pg.ConnectToAsync(db);
            await c.BackupFullAsync(path, compress: false, commandTimeoutSeconds: 300);
        }

        var t1 = BackupAsync(p1);
        var t2 = BackupAsync(p2);
        await Task.WhenAll(t1, t2);

        // Both files must verify cleanly.
        await using var verifier = await _pg.ConnectToAsync(db);
        foreach (var path in new[] { p1, p2 })
        {
            await using var cmd = verifier.CreateCommand();
            cmd.CommandText = "SELECT (dbbackup.pg_dbbackup_verify(@p)).is_valid";
            cmd.Parameters.AddWithValue("p", path);
            Assert.True((bool)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!,
                $"{path} should verify");
        }

        // Restore one of them; row count must match.
        var target = "race_full_r_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using (var admin = await _pg.AdminAsync())
            await using (var cmd = admin.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbrestore(ARRAY[@p]::text[], target_db := @tgt)";
                cmd.Parameters.AddWithValue("p", p1);
                cmd.Parameters.AddWithValue("tgt", target);
                cmd.CommandTimeout = 300;
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            NpgsqlConnection.ClearAllPools();
            await using var r = await _pg.ConnectToAsync(target);
            await using var c = r.CreateCommand();
            c.CommandText = "SELECT count(*) FROM t";
            Assert.Equal(5000L, (long)(await c.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Two_Simultaneous_Log_Backups_Chain_Stays_Consistent()
    {
        await using var setup = await _pg.CreateFreshDbWithExtensionAsync();
        var db = setup.Database!;
        await setup.SetModeFullAsync();
        await setup.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY, v text);" +
            "INSERT INTO t SELECT g, 'r' || g FROM generate_series(1, 1000) g;");

        var full = Helpers.BackupPath("race_log_full");
        await setup.BackupFullAsync(full, compress: false, commandTimeoutSeconds: 180);

        await setup.ExecAsync(
            "INSERT INTO t SELECT g, 'r' || g FROM generate_series(1001, 1500) g;");
        await setup.CloseAsync();

        var l1 = Helpers.BackupPath("race_log_a");
        var l2 = Helpers.BackupPath("race_log_b");

        async Task<Exception?> LogAsync(string path)
        {
            try
            {
                await using var c = await _pg.ConnectToAsync(db);
                await c.BackupLogAsync(path, full, compress: false, commandTimeoutSeconds: 300);
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }

        var t1 = LogAsync(l1);
        var t2 = LogAsync(l2);
        await Task.WhenAll(t1, t2);

        var r1 = await t1;
        var r2 = await t2;

        // At least one must succeed; the chain endpoint must be sane.
        Assert.True(r1 is null || r2 is null,
            $"both backups failed: {r1?.Message} / {r2?.Message}");

        // Chain endpoint == slot LSN invariant must hold.
        await using var verify = await _pg.ConnectToAsync(db);
        await using var cmd = verify.CreateCommand();
        cmd.CommandText =
            "SELECT lc.confirmed_lsn::text, rs.confirmed_flush_lsn::text " +
            "FROM dbbackup.logical_chains lc " +
            "JOIN pg_replication_slots rs ON rs.slot_name = lc.slot_name " +
            "WHERE lc.db_oid = (SELECT oid FROM pg_database " +
            "                   WHERE datname = current_database())";
        await using var rdr = await cmd.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(rdr.GetString(0), rdr.GetString(1));
    }
}
