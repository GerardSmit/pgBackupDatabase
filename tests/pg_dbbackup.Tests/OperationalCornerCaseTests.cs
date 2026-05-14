using System.Diagnostics;
using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class OperationalCornerCaseTests
{
    private readonly PgContainerFixture _pg;

    public OperationalCornerCaseTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Orphan_Bak_Tmp_Files_Swept()
    {
        await using var conn = await _pg.AdminAsync();
        var orphan = "/tmp/pg_dbbackup_orphan_" +
                     Guid.NewGuid().ToString("N")[..12] + ".bak.tmp";
        var orphanReal = "/tmp/pg_dbbackup_orphan_" +
                         Guid.NewGuid().ToString("N")[..12] + ".bak";
        await _pg.ShellAsync(
            $"echo orphan > {orphan} && echo orphan > {orphanReal} && " +
            $"chown postgres:postgres {orphan} {orphanReal} && " +
            $"touch -d '2 hours ago' {orphan} {orphanReal}");

        int swept;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT dbbackup.pg_dbbackup_sweep_orphan_tmp_files(0)";
            cmd.CommandTimeout = 60;
            swept = (int)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
        }
        Assert.True(swept >= 2, $"expected sweep ≥ 2, got {swept}");
        var stat = await _pg.ShellAsync(
            $"stat -c %n {orphan} {orphanReal} 2>/dev/null; echo done");
        Assert.Equal("done", stat.Stdout.Trim());
    }

    [Fact]
    public async Task Backend_Killed_Mid_Backup_Leaves_No_Final_Bak()
    {
        // Standin for "postgres restart mid-backup": kill the backup
        // backend, which exercises the same atomic-rename invariant
        // (orphan .bak.tmp may linger; final .bak must never exist).
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        var db = src.Database!;
        await src.ExecAsync(
            "CREATE TABLE big(id int PRIMARY KEY, payload text);" +
            "INSERT INTO big SELECT g, repeat(md5(g::text), 256) " +
            "FROM generate_series(1, 200000) g;", timeoutSeconds: 300);
        await src.CloseAsync();

        var path = Helpers.BackupPath("restart");
        var backupTask = Task.Run(async () =>
        {
            try
            {
                await using var c = await _pg.ConnectToAsync(db);
                await c.BackupFullAsync(path, compress: false,
                    commandTimeoutSeconds: 300);
            }
            catch { /* expected */ }
        });

        await Task.Delay(500, TestContext.Current.CancellationToken);
        await using (var admin = await _pg.AdminAsync())
        await using (var cmd = admin.CreateCommand())
        {
            cmd.CommandText =
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
                "WHERE datname = @d AND query LIKE '%pg_dbbackup%' " +
                "  AND pid <> pg_backend_pid()";
            cmd.Parameters.AddWithValue("d", db);
            await cmd.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
        }
        await backupTask;

        // Either backup completed cleanly (no kill landed during write) or
        // .bak does not exist (atomic rename never happened). Never a torn
        // partial .bak.
        var ls = await _pg.ShellAsync($"ls {path} 2>&1 || true");
        if (!ls.Stdout.Contains("No such file"))
        {
            // .bak exists; must verify cleanly.
            await using var verifier = await _pg.AdminAsync();
            await using var cmd = verifier.CreateCommand();
            cmd.CommandText =
                "SELECT (dbbackup.pg_dbbackup_verify(@p)).is_valid";
            cmd.Parameters.AddWithValue("p", path);
            Assert.True((bool)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
    }

    [Fact]
    public async Task Wal_Slot_Invalidation_Errors_Cleanly()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE w(id int PRIMARY KEY); INSERT INTO w VALUES (1);");

        var full = Helpers.BackupPath("wal_inv_full");
        await src.BackupFullAsync(full, compress: false);

        // Force slot invalidation by directly updating catalog state via
        // pg_replication_slot_advance + tiny WAL retention is fragile in
        // a docker test; instead simulate invalidation by dropping and
        // recreating the slot under a different name so the chain header
        // mismatches its slot. The next LOG must error rather than silently
        // produce a corrupt chain.
        await src.ExecAsync(
            "SELECT pg_drop_replication_slot(slot_name) " +
            "FROM pg_replication_slots " +
            "WHERE slot_name LIKE '_pg_dbbackup_%'");

        var log = Helpers.BackupPath("wal_inv_log");
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            src.BackupLogAsync(log, full));
        Assert.NotNull(ex.SqlState);
    }

    [Fact]
    public async Task Non_Superuser_Cannot_Run_Backup()
    {
        await using var admin = await _pg.AdminAsync();
        await admin.ExecAsync(
            "DO $$ BEGIN " +
            "IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='limited_user') " +
            "THEN CREATE ROLE limited_user LOGIN PASSWORD 'limited'; END IF; END $$;");
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        var db = src.Database!;
        await src.ExecAsync(
            "GRANT CONNECT ON DATABASE " + Quote(db) + " TO limited_user;");
        await src.CloseAsync();

        var b = new NpgsqlConnectionStringBuilder(_pg.ConnectionString)
        {
            Database = db,
            Username = "limited_user",
            Password = "limited",
        };
        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var conn = new NpgsqlConnection(b.ConnectionString);
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.BackupFullAsync(Helpers.BackupPath("nosuper"),
                compress: false);
        });
        Assert.NotNull(ex.SqlState);
    }

    [Fact]
    public async Task Many_Tables_RoundTrip()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "DO $$ BEGIN " +
            "FOR i IN 1..2000 LOOP " +
            "  EXECUTE format('CREATE TABLE t_%s(id int PRIMARY KEY)', i); " +
            "END LOOP; END $$;", timeoutSeconds: 600);

        var full = Helpers.BackupPath("many");
        await src.BackupFullAsync(full, compress: true,
            commandTimeoutSeconds: 900);
        await src.CloseAsync();

        var target = "many_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using (var admin = await _pg.AdminAsync())
            await using (var cmd = admin.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
                cmd.Parameters.AddWithValue("files", new[] { full });
                cmd.Parameters.AddWithValue("tgt", target);
                cmd.CommandTimeout = 900;
                await cmd.ExecuteNonQueryAsync(
                    TestContext.Current.CancellationToken);
            }
            NpgsqlConnection.ClearAllPools();
            await using var r = await _pg.ConnectToAsync(target);
            await using var c2 = r.CreateCommand();
            c2.CommandText =
                "SELECT count(*) FROM pg_class WHERE relkind='r' " +
                "  AND relname LIKE 't\\_%'";
            c2.CommandTimeout = 60;
            Assert.Equal(2000L, (long)(await c2.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally { try { await _pg.DropDbAsync(target); } catch { } }
    }

    [Fact]
    public async Task VacuumFull_Cluster_Reindex_Mid_Chain()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE v(id int PRIMARY KEY, payload text);" +
            "CREATE INDEX v_idx ON v(payload);" +
            "INSERT INTO v SELECT g, md5(g::text) FROM generate_series(1, 1000) g;");

        var full = Helpers.BackupPath("vac_full");
        await src.BackupFullAsync(full, compress: true);

        await src.ExecAsync("VACUUM FULL v;");
        await src.ExecAsync("CLUSTER v USING v_pkey;");
        await src.ExecAsync("REINDEX INDEX CONCURRENTLY v_idx;", timeoutSeconds: 120);
        await src.ExecAsync(
            "INSERT INTO v SELECT g, md5(g::text) FROM generate_series(1001, 1100) g;");

        var log = Helpers.BackupPath("vac_log");
        await src.BackupLogAsync(log, full, compress: true);
        await src.CloseAsync();

        var target = "vac_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using (var admin = await _pg.AdminAsync())
            await using (var cmd = admin.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
                cmd.Parameters.AddWithValue("files", new[] { full, log });
                cmd.Parameters.AddWithValue("tgt", target);
                cmd.CommandTimeout = 300;
                await cmd.ExecuteNonQueryAsync(
                    TestContext.Current.CancellationToken);
            }
            NpgsqlConnection.ClearAllPools();
            await using var r = await _pg.ConnectToAsync(target);
            await using var c2 = r.CreateCommand();
            c2.CommandText = "SELECT count(*) FROM v";
            Assert.Equal(1100L, (long)(await c2.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally { try { await _pg.DropDbAsync(target); } catch { } }
    }

    [Fact]
    public async Task Sixty_Four_Parallel_Backups_Across_Databases()
    {
        const int n = 64;
        var dbs = new string[n];
        for (int i = 0; i < n; i++)
        {
            await using var c = await _pg.CreateFreshDbWithExtensionAsync();
            dbs[i] = c.Database!;
            await c.ExecAsync(
                $"CREATE TABLE t(id int PRIMARY KEY); INSERT INTO t VALUES (1);");
        }

        async Task Backup(string db, string path)
        {
            await using var c = await _pg.ConnectToAsync(db);
            await c.BackupFullAsync(path, compress: false,
                commandTimeoutSeconds: 180);
        }

        var paths = dbs.Select((_, i) =>
            Helpers.BackupPath($"par_{i}")).ToArray();
        var tasks = dbs.Select((db, i) => Backup(db, paths[i])).ToArray();
        await Task.WhenAll(tasks);

        await using var verifier = await _pg.AdminAsync();
        foreach (var p in paths)
        {
            await using var cmd = verifier.CreateCommand();
            cmd.CommandText =
                "SELECT (dbbackup.pg_dbbackup_verify(@p)).is_valid";
            cmd.Parameters.AddWithValue("p", p);
            Assert.True((bool)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!,
                $"{p} should verify");
        }
    }

    [Fact]
    public async Task Encrypted_Bak_BitFlip_Rejected_On_Restore()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY, name text);" +
            "INSERT INTO t VALUES (1, 'foo');");

        var path = Helpers.BackupPath("flip");
        await src.BackupFullAsync(path, compress: false,
            password: "s3cret");
        await src.CloseAsync();

        // Flip a byte well inside the file (past header). Use dd within
        // the container.
        await _pg.ShellAsync(
            $"python3 -c \"import sys; " +
            $"f=open('{path}','r+b'); f.seek(800); b=f.read(1); " +
            $"f.seek(800); f.write(bytes([b[0] ^ 0xFF])); f.close()\" " +
            $"|| (size=$(stat -c%s {path}); off=$((size/2)); " +
            $"dd if=/dev/urandom of={path} bs=1 count=1 seek=$off conv=notrunc)");

        var target = "flip_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(ARRAY[@p]::text[], " +
                "  target_db := @tgt, password := 's3cret')";
            cmd.Parameters.AddWithValue("p", path);
            cmd.Parameters.AddWithValue("tgt", target);
            cmd.CommandTimeout = 60;
            var ex = await Assert.ThrowsAsync<PostgresException>(
                () => cmd.ExecuteNonQueryAsync(
                    TestContext.Current.CancellationToken));
            Assert.NotNull(ex.SqlState);
        }
        finally { try { await _pg.DropDbAsync(target); } catch { } }
    }

    [Fact]
    public async Task Restore_Force_Drops_Target_With_Active_Sessions()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY); INSERT INTO t VALUES (1);");
        var full = Helpers.BackupPath("fdrop");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "fdrop_" + Guid.NewGuid().ToString("N")[..8];
        await using (var admin = await _pg.AdminAsync())
        {
            await admin.ExecAsync($"CREATE DATABASE \"{target}\"");
        }
        await using var holder = await _pg.ConnectToAsync(target);
        try
        {
            await using (var admin = await _pg.AdminAsync())
            await using (var cmd = admin.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
                cmd.Parameters.AddWithValue("files", new[] { full });
                cmd.Parameters.AddWithValue("tgt", target);
                cmd.CommandTimeout = 120;
                await cmd.ExecuteNonQueryAsync(
                    TestContext.Current.CancellationToken);
            }
            NpgsqlConnection.ClearAllPools();
            await using var r = await _pg.ConnectToAsync(target);
            await using var c2 = r.CreateCommand();
            c2.CommandText = "SELECT count(*) FROM t";
            Assert.Equal(1L, (long)(await c2.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally
        {
            try { await holder.CloseAsync(); } catch { }
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Two_Concurrent_Restores_Different_Targets()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY); " +
            "INSERT INTO t SELECT g FROM generate_series(1, 200) g;");
        var full = Helpers.BackupPath("crest");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var t1 = "crest_a_" + Guid.NewGuid().ToString("N")[..8];
        var t2 = "crest_b_" + Guid.NewGuid().ToString("N")[..8];

        async Task DoRestore(string tgt)
        {
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
            cmd.Parameters.AddWithValue("files", new[] { full });
            cmd.Parameters.AddWithValue("tgt", tgt);
            cmd.CommandTimeout = 180;
            await cmd.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
        }

        try
        {
            await Task.WhenAll(DoRestore(t1), DoRestore(t2));
            NpgsqlConnection.ClearAllPools();
            await using var c1 = await _pg.ConnectToAsync(t1);
            await using var c2 = await _pg.ConnectToAsync(t2);
            foreach (var c in new[] { c1, c2 })
            {
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM t";
                Assert.Equal(200L, (long)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(t1); } catch { }
            try { await _pg.DropDbAsync(t2); } catch { }
        }
    }

    private static string Quote(string n) => "\"" + n.Replace("\"", "\"\"") + "\"";
}
