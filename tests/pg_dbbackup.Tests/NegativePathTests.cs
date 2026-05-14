using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Coverage for error paths that previously had no explicit tests:
/// path-traversal rejection at SQL entry points, corrupted/truncated
/// .bak detection, wrong-password behaviour, slot-drop recovery, and the
/// chain-endpoint vs slot.confirmed_flush_lsn invariant under failure.
/// </summary>
public sealed class NegativePathTests
{
    private readonly PgContainerFixture _pg;

    public NegativePathTests(PgContainerFixture pg) => _pg = pg;

    // -------- L1: path-traversal / NUL / empty path validation --------

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("/tmp/../etc/passwd")]
    [InlineData("/tmp/x/../../etc/passwd")]
    [InlineData("..")]
    [InlineData("")]
    public async Task Backup_Rejects_Path_With_DotDot_Or_Empty(string badPath)
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbbackup(@db, @path, type := 'full')";
            cmd.Parameters.AddWithValue("db", conn.Database!);
            cmd.Parameters.AddWithValue("path", badPath);
            await cmd.ExecuteNonQueryAsync();
        });

        Assert.Equal("22023", ex.SqlState);
    }

    [Fact]
    public async Task Restore_Rejects_Path_With_DotDot()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(ARRAY[@path]::text[])";
            cmd.Parameters.AddWithValue("db", conn.Database!);
            cmd.Parameters.AddWithValue("path", "/tmp/../etc/passwd");
            await cmd.ExecuteNonQueryAsync();
        });

        Assert.Equal("22023", ex.SqlState);
    }

    [Fact]
    public async Task Async_Backup_Rejects_Path_With_DotDot()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbbackup_async(@db, @path, 'full', false)";
            cmd.Parameters.AddWithValue("db", conn.Database!);
            cmd.Parameters.AddWithValue("path", "/tmp/../etc/passwd");
            await cmd.ExecuteNonQueryAsync();
        });

        Assert.Equal("22023", ex.SqlState);
    }

    // -------- corrupted / truncated .bak detection --------

    [Fact]
    public async Task Verify_Reports_False_On_Flipped_Magic()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.ExecAsync("CREATE TABLE t(id int);");
        var path = Helpers.BackupPath("negative");
        await conn.BackupFullAsync(path);

        // The .bak is owned by the postgres user with no write bits for
        // others. Stage a corrupted copy in /tmp, then mv it into place
        // (which only needs +w on the directory).
        var corrupt = await _pg.ShellAsync(
            $"set -e; " +
            $"printf '\\x00' > {path}.head; " +
            $"dd if={path} of={path}.tail bs=1 skip=1 status=none; " +
            $"cat {path}.head {path}.tail > {path}.bad; " +
            $"mv {path}.bad {path}; " +
            $"rm -f {path}.head {path}.tail");
        Assert.True(corrupt.ExitCode == 0,
            $"corrupt failed: stdout={corrupt.Stdout} stderr={corrupt.Stderr}");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT is_valid, detail FROM dbbackup.pg_dbbackup_verify(@path)";
        cmd.Parameters.AddWithValue("path", path);
        await using var rdr = await cmd.ExecuteReaderAsync();
        Assert.True(await rdr.ReadAsync());
        Assert.False(rdr.GetBoolean(0));
    }

    [Fact]
    public async Task Verify_Reports_False_On_Truncated_Bak()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.ExecAsync(
            "CREATE TABLE t(id int);" +
            "INSERT INTO t SELECT generate_series(1, 100);");
        var path = Helpers.BackupPath("negative");
        await conn.BackupFullAsync(path);

        // Shave 128 bytes off the tail — long enough to destroy the
        // footer magic + SHA-256 but short enough not to wipe the
        // sections entirely.
        var truncate = await _pg.ShellAsync(
            $"set -e; sz=$(stat -c %s {path}); newsz=$((sz - 128)); " +
            $"dd if={path} of={path}.trunc bs=1 count=$newsz status=none; " +
            $"mv {path}.trunc {path}");
        Assert.Equal(0, truncate.ExitCode);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT is_valid FROM dbbackup.pg_dbbackup_verify(@path)";
        cmd.Parameters.AddWithValue("path", path);
        var ok = (bool)(await cmd.ExecuteScalarAsync())!;
        Assert.False(ok);
    }

    [Fact]
    public async Task Restore_Rejects_Wrong_Password()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.ExecAsync(
            "CREATE TABLE t(id int);" +
            "INSERT INTO t SELECT generate_series(1, 3);");

        var path = Helpers.BackupPath("negative");
        await conn.BackupFullAsync(path, compress: true, password: "correct-horse");

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(ARRAY[@path]::text[], " +
                "target_db := @target, password := @pw)";
            cmd.Parameters.AddWithValue("db", conn.Database!);
            cmd.Parameters.AddWithValue("path", path);
            cmd.Parameters.AddWithValue("target", conn.Database! + "_wrongpw");
            cmd.Parameters.AddWithValue("pw", "wrong-horse");
            cmd.CommandTimeout = 60;
            await cmd.ExecuteNonQueryAsync();
        });

        Assert.NotNull(ex.SqlState);
    }

    [Fact]
    public async Task Verify_Rejects_Missing_Password()
    {
        // Encrypted backup verified without a password should not succeed.
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.ExecAsync("CREATE TABLE t(id int);");
        var path = Helpers.BackupPath("negative");
        await conn.BackupFullAsync(path, compress: true, password: "secret");

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(ARRAY[@path]::text[], " +
                "target_db := @target)";
            cmd.Parameters.AddWithValue("db", conn.Database!);
            cmd.Parameters.AddWithValue("path", path);
            cmd.Parameters.AddWithValue("target", conn.Database! + "_nopw");
            cmd.CommandTimeout = 60;
            await cmd.ExecuteNonQueryAsync();
        });

        Assert.NotNull(ex.SqlState);
    }

    // -------- chain integrity --------

    [Fact]
    public async Task Log_Rejects_Stale_Base_File()
    {
        // FULL ➜ LOG1 ➜ LOG2, then attempt LOG3 from FULL: the chain
        // endpoint is now past FULL.stop_lsn so the extension must reject
        // the request rather than silently produce an unrestorable .bak.
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.SetModeFullAsync();
        await conn.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY, v text);" +
            "INSERT INTO t SELECT g, 'r'||g FROM generate_series(1, 5) g;");

        var full = Helpers.BackupPath("negative");
        await conn.BackupFullAsync(full);

        await conn.ExecAsync("INSERT INTO t VALUES (100, 'late');");
        var log1 = Helpers.BackupPath("negative");
        await conn.BackupLogAsync(log1, full);

        await conn.ExecAsync("INSERT INTO t VALUES (101, 'later');");
        var log2 = Helpers.BackupPath("negative");
        await conn.BackupLogAsync(log2, log1);

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbbackup(@db, @path, type := 'log', " +
                "base_filepath := @base)";
            cmd.Parameters.AddWithValue("db", conn.Database!);
            cmd.Parameters.AddWithValue("path", Helpers.BackupPath("negative"));
            cmd.Parameters.AddWithValue("base", full);
            cmd.CommandTimeout = 60;
            await cmd.ExecuteNonQueryAsync();
        });

        Assert.Equal("55000", ex.SqlState);
    }

    [Fact]
    public async Task Slot_Drop_Breaks_Chain_Until_New_Full()
    {
        // Take FULL, hand-drop the chain slot, attempt a LOG: the
        // extension must refuse rather than crash, and a fresh FULL must
        // recover the chain.
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.SetModeFullAsync();
        await conn.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY);" +
            "INSERT INTO t SELECT generate_series(1, 3);");

        var full = Helpers.BackupPath("negative");
        await conn.BackupFullAsync(full);

        await using (var dropCmd = conn.CreateCommand())
        {
            dropCmd.CommandText =
                "SELECT pg_drop_replication_slot('_pg_dbbackup_' || " +
                "(SELECT oid::text FROM pg_database WHERE datname = current_database()))";
            await dropCmd.ExecuteNonQueryAsync();
        }

        await conn.ExecAsync("INSERT INTO t VALUES (100);");

        // Without the slot, LOG must error. Different error codes are
        // acceptable so long as the call is rejected.
        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await conn.BackupLogAsync(Helpers.BackupPath("negative"), full);
        });
        Assert.NotNull(ex.SqlState);

        // Recovery: a new FULL recreates the slot and chain. Subsequent
        // LOG against the new FULL succeeds.
        var fresh = Helpers.BackupPath("negative");
        await conn.BackupFullAsync(fresh);
        await conn.ExecAsync("INSERT INTO t VALUES (200);");
        var log = Helpers.BackupPath("negative");
        await conn.BackupLogAsync(log, fresh);
    }

    [Fact]
    public async Task After_Successful_Log_Slot_Confirmed_Matches_Chain()
    {
        // Invariant under the inverted slot/chain ordering: after every
        // chain-extending backup, pg_replication_slots.confirmed_flush_lsn
        // must equal logical_chains.confirmed_lsn for the affected slot.
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.SetModeFullAsync();
        await conn.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY);" +
            "INSERT INTO t SELECT generate_series(1, 5);");

        var full = Helpers.BackupPath("negative");
        await conn.BackupFullAsync(full);
        await conn.ExecAsync("INSERT INTO t VALUES (100);");
        var log = Helpers.BackupPath("negative");
        await conn.BackupLogAsync(log, full);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT lc.confirmed_lsn::text, rs.confirmed_flush_lsn::text " +
            "FROM dbbackup.logical_chains lc " +
            "JOIN pg_replication_slots rs ON rs.slot_name = lc.slot_name " +
            "WHERE lc.db_oid = (SELECT oid FROM pg_database " +
            "                   WHERE datname = current_database())";
        await using var rdr = await cmd.ExecuteReaderAsync();
        Assert.True(await rdr.ReadAsync());
        var chainLsn = rdr.GetString(0);
        var slotLsn = rdr.GetString(1);
        Assert.Equal(chainLsn, slotLsn);
    }

    [Fact]
    public async Task Chain_Upsert_Failure_Leaves_Slot_Not_Ahead_Of_Chain()
    {
        // H1 invariant: chain row written before pg_replication_slot_advance,
        // so if the upsert raises, the advance must NOT have run. The slot's
        // confirmed_flush_lsn must remain <= logical_chains.confirmed_lsn
        // (i.e. the previous chain endpoint), never ahead.
        //
        // We force the upsert to fail mid-flight with a BEFORE INSERT/UPDATE
        // trigger on dbbackup.logical_chains that raises an exception. The
        // LOG backup must propagate that error and leave the chain row at
        // L1 (the previous FULL's stop_lsn) while the slot is also still at L1.
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await conn.SetModeFullAsync();
        await conn.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY);" +
            "INSERT INTO t SELECT generate_series(1, 3);");

        var full = Helpers.BackupPath("negative");
        await conn.BackupFullAsync(full);

        // Capture L1 = chain endpoint right after the FULL.
        string l1Chain, l1Slot, slotName;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT lc.confirmed_lsn::text, rs.confirmed_flush_lsn::text, " +
                "       lc.slot_name " +
                "FROM dbbackup.logical_chains lc " +
                "JOIN pg_replication_slots rs ON rs.slot_name = lc.slot_name " +
                "WHERE lc.db_oid = (SELECT oid FROM pg_database " +
                "                   WHERE datname = current_database())";
            await using var rdr = await cmd.ExecuteReaderAsync();
            Assert.True(await rdr.ReadAsync());
            l1Chain = rdr.GetString(0);
            l1Slot = rdr.GetString(1);
            slotName = rdr.GetString(2);
            Assert.Equal(l1Chain, l1Slot);
        }

        // Block both new inserts and the ON CONFLICT update path. The
        // trigger function must live in a schema visible from the backend
        // that runs the upsert (the calling backend, which is `conn`), so
        // create it in dbbackup's own schema using a persistent name.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE OR REPLACE FUNCTION dbbackup.pg_dbbackup_test_block_upsert()
                RETURNS trigger AS $$
                BEGIN
                  RAISE EXCEPTION 'simulated upsert failure for test';
                END;
                $$ LANGUAGE plpgsql;
                DROP TRIGGER IF EXISTS pg_dbbackup_test_block ON dbbackup.logical_chains;
                CREATE TRIGGER pg_dbbackup_test_block
                BEFORE INSERT OR UPDATE ON dbbackup.logical_chains
                FOR EACH ROW EXECUTE FUNCTION dbbackup.pg_dbbackup_test_block_upsert();";
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            await conn.ExecAsync("INSERT INTO t VALUES (100);");

            var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await conn.BackupLogAsync(Helpers.BackupPath("negative"), full);
            });
            Assert.Contains("simulated upsert failure for test", ex.MessageText);

            // After the failure, the chain row and slot must both still be
            // at L1. Critically the slot must NOT be ahead of the chain —
            // that would be the data-loss state the H1 fix prevents.
            string chainLsnAfter, slotLsnAfter;
            await using (var verify = conn.CreateCommand())
            {
                verify.CommandText =
                    "SELECT lc.confirmed_lsn::text, rs.confirmed_flush_lsn::text " +
                    "FROM dbbackup.logical_chains lc " +
                    "JOIN pg_replication_slots rs ON rs.slot_name = lc.slot_name " +
                    "WHERE lc.slot_name = @slot";
                verify.Parameters.AddWithValue("slot", slotName);
                await using var rdr = await verify.ExecuteReaderAsync();
                Assert.True(await rdr.ReadAsync());
                chainLsnAfter = rdr.GetString(0);
                slotLsnAfter = rdr.GetString(1);
            }

            Assert.Equal(l1Chain, chainLsnAfter);
            // Slot may equal chain (no advance ever issued) or — in
            // theory — be earlier; it must never be strictly ahead.
            await using var cmpCmd = conn.CreateCommand();
            cmpCmd.CommandText = "SELECT @slotL::pg_lsn <= @chainL::pg_lsn";
            cmpCmd.Parameters.AddWithValue("slotL", slotLsnAfter);
            cmpCmd.Parameters.AddWithValue("chainL", chainLsnAfter);
            var notAhead = (bool)(await cmpCmd.ExecuteScalarAsync())!;
            Assert.True(notAhead,
                $"slot {slotLsnAfter} must not be ahead of chain {chainLsnAfter}");
        }
        finally
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "DROP TRIGGER IF EXISTS pg_dbbackup_test_block ON dbbackup.logical_chains; " +
                "DROP FUNCTION IF EXISTS dbbackup.pg_dbbackup_test_block_upsert()";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
