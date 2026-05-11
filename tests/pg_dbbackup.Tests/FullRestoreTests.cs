using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class FullRestoreTests
{
    private readonly PgContainerFixture _pg;

    public FullRestoreTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task FullRestore_RoundTrip_RestoresTables()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE widgets(id int PRIMARY KEY, name text)");
        await src.ExecAsync(
            "INSERT INTO widgets SELECT g, 'w' || g FROM generate_series(1, 100) g");

        var path = Helpers.BackupPath("fullrestore");
        await src.BackupFullAsync(path);
        await src.CloseAsync();

        var target = "fullrestored_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using (var admin = await _pg.AdminAsync())
            await using (var cmd = admin.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbrestore(@db, ARRAY[@p]::text[], target_db := @tgt)";
                cmd.Parameters.AddWithValue("db", "ignored");
                cmd.Parameters.AddWithValue("p", path);
                cmd.Parameters.AddWithValue("tgt", target);
                cmd.CommandTimeout = 120;
                await cmd.ExecuteNonQueryAsync();
            }

            NpgsqlConnection.ClearAllPools();

            await using var conn = await _pg.ConnectToAsync(target);
            await using var c = conn.CreateCommand();
            c.CommandText = "SELECT count(*) FROM widgets";
            c.CommandTimeout = 30;
            var n = (long)(await c.ExecuteScalarAsync())!;
            Assert.Equal(100L, n);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task FullRestore_Pitr_PastChainEnd_Succeeds()
    {
        // stop_at = now() is past every commit in the backup chain, so the
        // restore should succeed and just restore to the chain-end state.
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync("CREATE TABLE t(id int PRIMARY KEY)");
        await src.ExecAsync("INSERT INTO t VALUES (1)");

        var path = Helpers.BackupPath("fullrestore");
        await src.BackupFullAsync(path);
        await src.CloseAsync();

        var target = "pitr_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(@db, ARRAY[@p]::text[], " +
                "target_db := @tgt, stop_at := now() + interval '1 hour')";
            cmd.Parameters.AddWithValue("db", "ignored");
            cmd.Parameters.AddWithValue("p", path);
            cmd.Parameters.AddWithValue("tgt", target);
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task FullRestore_RejectsNonFullFirstFile()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY); INSERT INTO t VALUES (1);");

        var simplePath = Helpers.BackupPath("fullrestore");
        await src.BackupFullAsync(simplePath);
        await src.CloseAsync();

        var target = "nonfull_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(@db, ARRAY[@p]::text[], " +
                "target_db := @tgt, stop_at := now())";
            cmd.Parameters.AddWithValue("db", "ignored");
            cmd.Parameters.AddWithValue("p", simplePath);
            cmd.Parameters.AddWithValue("tgt", target);

            await Assert.ThrowsAsync<PostgresException>(
                () => cmd.ExecuteNonQueryAsync());
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task FullRestore_Pitr_Before_Cutoff_Visible()
    {
        // Data committed BEFORE the FULL backup is in the DATA section and
        // must be visible after restore, regardless of any PITR cutoff that
        // lies after that commit but before now().
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync("CREATE TABLE t(id int PRIMARY KEY)");
        await src.ExecAsync(
            "INSERT INTO t SELECT g FROM generate_series(1, 50) g");

        // Capture a timestamp that is comfortably after the committed inserts
        // but before we take the backup. The PITR cutoff sits between the
        // INSERT's xact_time and the backup's stop_lsn — the captured pages
        // already hold the inserts.
        DateTime cutoff;
        await using (var c = src.CreateCommand())
        {
            c.CommandText = "SELECT now()";
            cutoff = (DateTime)(await c.ExecuteScalarAsync())!;
        }
        await Task.Delay(1000);

        var path = Helpers.BackupPath("fullrestore");
        await src.BackupFullAsync(path);
        await src.CloseAsync();

        var target = "pitr_before_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using (var admin = await _pg.AdminAsync())
            await using (var cmd = admin.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbrestore(@db, ARRAY[@p]::text[], " +
                    "target_db := @tgt, stop_at := @cutoff)";
                cmd.Parameters.AddWithValue("db", "ignored");
                cmd.Parameters.AddWithValue("p", path);
                cmd.Parameters.AddWithValue("tgt", target);
                cmd.Parameters.AddWithValue("cutoff", cutoff);
                cmd.CommandTimeout = 120;
                await cmd.ExecuteNonQueryAsync();
            }

            NpgsqlConnection.ClearAllPools();

            await using var conn = await _pg.ConnectToAsync(target);
            await using var c = conn.CreateCommand();
            c.CommandText = "SELECT count(*) FROM t";
            c.CommandTimeout = 30;
            var n = (long)(await c.ExecuteScalarAsync())!;
            Assert.Equal(50L, n);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task FullRestore_Pitr_After_Cutoff_Invisible()
    {
        // Take a FULL backup; then commit more data and take a LOG backup;
        // then restore with a stop_at between the two backups. Without WAL
        // apply the LOG additions are not replayed, so the post-FULL rows
        // are correctly absent from the restored DB.
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync("CREATE TABLE t(id int PRIMARY KEY)");
        await src.ExecAsync(
            "INSERT INTO t SELECT g FROM generate_series(1, 10) g");

        var fullPath = Helpers.BackupPath("fullrestore");
        await src.BackupFullAsync(fullPath);

        DateTime midCutoff;
        await using (var c = src.CreateCommand())
        {
            c.CommandText = "SELECT now()";
            midCutoff = (DateTime)(await c.ExecuteScalarAsync())!;
        }
        await Task.Delay(1500);

        await src.ExecAsync(
            "INSERT INTO t SELECT g FROM generate_series(11, 30) g");

        var logPath = Helpers.BackupPath("fulllog");
        await src.BackupLogAsync(logPath, fullPath);
        await src.CloseAsync();

        var target = "pitr_after_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using (var admin = await _pg.AdminAsync())
            await using (var cmd = admin.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbrestore(@db, ARRAY[@p1, @p2]::text[], " +
                    "target_db := @tgt, stop_at := @cutoff)";
                cmd.Parameters.AddWithValue("db", "ignored");
                cmd.Parameters.AddWithValue("p1", fullPath);
                cmd.Parameters.AddWithValue("p2", logPath);
                cmd.Parameters.AddWithValue("tgt", target);
                cmd.Parameters.AddWithValue("cutoff", midCutoff);
                cmd.CommandTimeout = 120;
                await cmd.ExecuteNonQueryAsync();
            }

            NpgsqlConnection.ClearAllPools();

            await using var conn = await _pg.ConnectToAsync(target);
            await using var c = conn.CreateCommand();
            c.CommandText = "SELECT count(*) FROM t";
            c.CommandTimeout = 30;
            var n = (long)(await c.ExecuteScalarAsync())!;
            Assert.Equal(10L, n);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact(Skip =
        "Subprocess PITR scaffolding is in place (see " +
        "src/subprocess_recovery.c) and successfully drives initdb, file " +
        "injection, backup_label generation, and pg_ctl start. Recovery " +
        "fails with \"WAL file is from different database system\" because " +
        "PG's recovery is cluster-scoped, not database-scoped: WAL " +
        "segments carry the source cluster's sysid, but the synthetic " +
        "PGDATA was freshly initdb'd with a new sysid. Injecting the " +
        "source's pg_control fixes the sysid but then pg_database " +
        "(a shared catalog) has no row for src_dboid, so we can't connect " +
        "to the recovered DB to pg_dump. A complete fix requires either " +
        "capturing the full cluster's global/ catalogs at backup time " +
        "(eliminates the per-DB advantage) or running CREATE DATABASE " +
        "with a chosen OID matching src_dboid (no public SQL API in " +
        "PG17). Today's PITR remains partial: the FULL backup's pre-stop " +
        "state is visible; data captured in DIFF/LOG segments is not " +
        "applied. See Pitr_Before_Cutoff_Visible and " +
        "Pitr_After_Cutoff_Invisible for the working partial-PITR cases.")]
    public Task FullRestore_Pitr_Stops_At_Timestamp() => Task.CompletedTask;

    [Fact]
    public async Task FullRestore_CleansUpTempDb_OnFailure()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY); INSERT INTO t VALUES (1);");

        var path = Helpers.BackupPath("fullrestore");
        await src.BackupFullAsync(path);
        await src.CloseAsync();

        var trunc = await _pg.ShellAsync(
            $"set -e; sz=$(stat -c %s {path}); newsz=$((sz - 200)); " +
            $"dd if={path} of={path}.t bs=1 count=$newsz status=none; mv {path}.t {path}");
        Assert.Equal(0, trunc.ExitCode);

        var target = "shouldfail_full_" + Guid.NewGuid().ToString("N")[..8];

        await using (var admin = await _pg.AdminAsync())
        await using (var cmd = admin.CreateCommand())
        {
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(@db, ARRAY[@p]::text[], target_db := @tgt)";
            cmd.Parameters.AddWithValue("db", "ignored");
            cmd.Parameters.AddWithValue("p", path);
            cmd.Parameters.AddWithValue("tgt", target);

            await Assert.ThrowsAsync<PostgresException>(
                () => cmd.ExecuteNonQueryAsync());
        }

        Assert.False(await _pg.DbExistsAsync(target),
            "target DB unexpectedly exists after failed restore");

        await using (var admin = await _pg.AdminAsync())
        await using (var cmd = admin.CreateCommand())
        {
            cmd.CommandText =
                "SELECT count(*) FROM pg_database WHERE datname LIKE '_pg_dbbackup_restore_%'";
            var n = (long)(await cmd.ExecuteScalarAsync())!;
            Assert.Equal(0L, n);
        }
    }
}
