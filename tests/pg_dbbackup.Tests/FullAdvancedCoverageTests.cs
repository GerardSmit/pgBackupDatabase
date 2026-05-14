using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class FullAdvancedCoverageTests
{
    private readonly PgContainerFixture _pg;

    public FullAdvancedCoverageTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task FullRestore_Replays_Multiple_Log_Backups_In_Order()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY, v text);" +
            "INSERT INTO t VALUES (1, 'full');");

        var full = Helpers.BackupPath("multi_log");
        await src.BackupFullAsync(full);

        await src.ExecAsync("INSERT INTO t VALUES (2, 'log1');");
        var log1 = Helpers.BackupPath("multi_log");
        await src.BackupLogAsync(log1, full);

        await src.ExecAsync("INSERT INTO t VALUES (3, 'log2');");
        var log2 = Helpers.BackupPath("multi_log");
        await src.BackupLogAsync(log2, log1);
        await src.CloseAsync();

        var target = "multi_log_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full, log1, log2);

            await using var conn = await _pg.ConnectToAsync(target);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT array_agg(id ORDER BY id) FROM t";
            var ids = (int[])(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            Assert.Equal(new[] { 1, 2, 3 }, ids);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task FullRestore_Branch_After_Restore_Uses_Full_Log1_Log3()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        var dbName = src.Database!;
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY, v text);" +
            "INSERT INTO t VALUES (1, 'full');");

        var full = Helpers.BackupPath("branch");
        await src.BackupFullAsync(full);

        await src.ExecAsync("INSERT INTO t VALUES (2, 'log1');");
        var log1 = Helpers.BackupPath("branch");
        await src.BackupLogAsync(log1, full);

        await src.ExecAsync("INSERT INTO t VALUES (3, 'abandoned-log2');");
        var log2 = Helpers.BackupPath("branch");
        await src.BackupLogAsync(log2, log1);
        await src.CloseAsync();

        await RestoreAsync(dbName, full, log1);

        await using (var restored = await _pg.ConnectToAsync(dbName))
        {
            await restored.ExecAsync("INSERT INTO t VALUES (4, 'branch-log3');");
            var log3 = Helpers.BackupPath("branch");
            await restored.BackupLogAsync(log3, log1);
            await restored.CloseAsync();

            var target = "branch_verify_" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                await RestoreAsync(target, full, log1, log3);

                await using var verify = await _pg.ConnectToAsync(target);
                await using var cmd = verify.CreateCommand();
                cmd.CommandText = "SELECT array_agg(id ORDER BY id) FROM t";
                var ids = (int[])(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
                Assert.Equal(new[] { 1, 2, 4 }, ids);
            }
            finally
            {
                try { await _pg.DropDbAsync(target); } catch { }
            }
        }
    }

    [Fact]
    public async Task FullRestore_Preserves_Indexes_Owners_And_Grants()
    {
        var roleName = "restore_role_" + Guid.NewGuid().ToString("N")[..8];
        string? sourceDb = null;

        await using (var admin = await _pg.AdminAsync())
        await using (var cmd = admin.CreateCommand())
        {
            cmd.CommandText = $"CREATE ROLE \"{roleName}\"";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        sourceDb = src.Database!;
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE SCHEMA app;" +
            "CREATE TABLE app.secure(id int PRIMARY KEY, email text NOT NULL, payload text);" +
            "CREATE UNIQUE INDEX secure_email_idx ON app.secure(email);" +
            "CREATE INDEX secure_payload_expr_idx ON app.secure((lower(payload)));" +
            $"ALTER TABLE app.secure OWNER TO \"{roleName}\";" +
            $"GRANT SELECT ON app.secure TO \"{roleName}\";" +
            "INSERT INTO app.secure VALUES (1, 'a@example.com', 'Alpha');");

        var full = Helpers.BackupPath("owners");
        await src.BackupFullAsync(full);
        await src.CloseAsync();

        await _pg.DropDbAsync(sourceDb);
        sourceDb = null;
        await using (var admin = await _pg.AdminAsync())
        await using (var cmd = admin.CreateCommand())
        {
            cmd.CommandText = $"DROP ROLE \"{roleName}\"";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var target = "owners_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            var notices = new List<string>();
            await RestoreAsync(target, notices, full);
            Assert.Contains(notices, n =>
                n.Contains(roleName, StringComparison.Ordinal) &&
                n.Contains("NOLOGIN placeholder role", StringComparison.Ordinal));

            await using var conn = await _pg.ConnectToAsync(target);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM pg_indexes " +
                    "WHERE schemaname = 'app' " +
                    "AND indexname IN ('secure_email_idx', 'secure_payload_expr_idx')";
                Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT pg_get_userbyid(c.relowner) " +
                    "FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
                    "WHERE n.nspname = 'app' AND c.relname = 'secure'";
                Assert.Equal(roleName, (string)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT has_table_privilege(@role, 'app.secure', 'SELECT')";
                cmd.Parameters.AddWithValue("role", roleName);
                Assert.True((bool)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
            if (sourceDb is not null)
            {
                try { await _pg.DropDbAsync(sourceDb); } catch { }
            }
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"DROP ROLE IF EXISTS \"{roleName}\"";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task FullRestore_Restores_Extension_Metadata()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE EXTENSION pgcrypto;" +
            "CREATE TABLE docs(id uuid PRIMARY KEY DEFAULT gen_random_uuid(), body text);" +
            "INSERT INTO docs(body) VALUES ('extension-backed default');");

        var full = Helpers.BackupPath("extension");
        await src.BackupFullAsync(full);
        await src.CloseAsync();

        var target = "extension_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);

            await using var conn = await _pg.ConnectToAsync(target);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM pg_extension WHERE extname = 'pgcrypto'";
                Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO docs(body) VALUES ('after restore') RETURNING id IS NOT NULL";
                Assert.True((bool)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task FullRestore_Replays_Ddl_Created_During_Log_Window()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE existing_items(id int PRIMARY KEY, v text);" +
            "INSERT INTO existing_items VALUES (1, 'full');");

        var full = Helpers.BackupPath("ddl_log");
        await src.BackupFullAsync(full);

        await src.ExecAsync(
            "CREATE TABLE post_full_items(id int PRIMARY KEY, v text);");
        await src.ExecAsync(
            "INSERT INTO post_full_items VALUES (2, 'created after full');");

        var log = Helpers.BackupPath("ddl_log");
        await src.BackupLogAsync(log, full);
        await src.CloseAsync();

        var target = "ddl_log_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full, log);

            await using var conn = await _pg.ConnectToAsync(target);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT v FROM post_full_items WHERE id = 2";
            Assert.Equal("created after full", (string)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task FullRestore_Pitr_Repairs_Owned_Sequence_At_Stop_Time()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE seq_items(id bigserial PRIMARY KEY, v text);" +
            "INSERT INTO seq_items(v) VALUES ('full');");

        var full = Helpers.BackupPath("seq_stop");
        await src.BackupFullAsync(full);

        await Task.Delay(1200, TestContext.Current.CancellationToken);
        await src.ExecAsync("INSERT INTO seq_items(v) VALUES ('before cutoff');");

        DateTime cutoff;
        await using (var c = src.CreateCommand())
        {
            c.CommandText = "SELECT clock_timestamp()";
            cutoff = (DateTime)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        }

        await Task.Delay(1200, TestContext.Current.CancellationToken);
        await src.ExecAsync("INSERT INTO seq_items(v) VALUES ('after cutoff');");

        var log = Helpers.BackupPath("seq_stop");
        await src.BackupLogAsync(log, full);
        await src.CloseAsync();

        var target = "seq_stop_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full, log, cutoff);

            await using var conn = await _pg.ConnectToAsync(target);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*), max(id) FROM seq_items";
                await using var rdr = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
                Assert.Equal(2L, rdr.GetInt64(0));
                Assert.Equal(2L, rdr.GetInt64(1));
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO seq_items(v) VALUES ('next') RETURNING id";
                Assert.Equal(3L, (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task FullRestore_Pitr_Replays_Standalone_Sequence_State_At_Stop_Time()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync("CREATE SEQUENCE loose_seq START WITH 10;");

        var full = Helpers.BackupPath("standalone_seq_stop");
        await src.BackupFullAsync(full);

        await Task.Delay(1200, TestContext.Current.CancellationToken);
        await src.ExecAsync("SELECT nextval('loose_seq');");

        DateTime cutoff;
        await using (var c = src.CreateCommand())
        {
            c.CommandText = "SELECT clock_timestamp()";
            cutoff = (DateTime)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        }

        await Task.Delay(1200, TestContext.Current.CancellationToken);
        await src.ExecAsync("SELECT nextval('loose_seq');");

        var log = Helpers.BackupPath("standalone_seq_stop");
        await src.BackupLogAsync(log, full);
        await src.CloseAsync();

        var target = "standalone_seq_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full, log, cutoff);

            await using var conn = await _pg.ConnectToAsync(target);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT nextval('loose_seq')";
            Assert.Equal(11L, (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task FullRestore_Larger_Table_With_Index_RoundTrip()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE big_data(id int PRIMARY KEY, bucket int NOT NULL, payload text NOT NULL);" +
            "CREATE INDEX big_data_bucket_idx ON big_data(bucket);" +
            "INSERT INTO big_data " +
            "SELECT g, g % 128, repeat(md5(g::text), 8) " +
            "FROM generate_series(1, 50000) g;",
            timeoutSeconds: 120);

        var full = Helpers.BackupPath("large_full");
        await src.BackupFullAsync(full, compress: true, commandTimeoutSeconds: 300);
        await src.CloseAsync();

        var target = "large_full_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);

            await using var conn = await _pg.ConnectToAsync(target);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*), count(DISTINCT bucket) FROM big_data";
                cmd.CommandTimeout = 60;
                await using var rdr = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
                Assert.Equal(50000L, rdr.GetInt64(0));
                Assert.Equal(128L, rdr.GetInt64(1));
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM pg_indexes " +
                    "WHERE tablename = 'big_data' AND indexname = 'big_data_bucket_idx'";
                Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task FullRestore_Restores_LargeObjects_From_Full_And_Log()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "SELECT lo_from_bytea(987650::oid, convert_to('full large object', 'UTF8'));");

        var full = Helpers.BackupPath("large_objects");
        await src.BackupFullAsync(full);

        await src.ExecAsync(
            "SELECT lo_from_bytea(987651::oid, convert_to('log large object', 'UTF8'));");

        var log = Helpers.BackupPath("large_objects");
        await src.BackupLogAsync(log, full);
        await src.CloseAsync();

        var target = "large_objects_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full, log);

            await using var conn = await _pg.ConnectToAsync(target);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT convert_from(lo_get(987650::oid), 'UTF8')";
                Assert.Equal("full large object", (string)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT convert_from(lo_get(987651::oid), 'UTF8')";
                Assert.Equal("log large object", (string)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task FullRestore_Pitr_Replays_LargeObject_Writes_And_Unlinks()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "SELECT lo_from_bytea(987660::oid, convert_to('aaaaaaaaaa', 'UTF8'));" +
            "SELECT lo_from_bytea(987662::oid, convert_to('remove me', 'UTF8'));");

        var full = Helpers.BackupPath("large_object_pitr");
        await src.BackupFullAsync(full);

        await Task.Delay(1200, TestContext.Current.CancellationToken);
        await src.ExecAsync(
            "SELECT lo_put(987660::oid, 0, convert_to('bbbbbbbbbb', 'UTF8'));" +
            "SELECT lo_from_bytea(987661::oid, convert_to('before cutoff', 'UTF8'));" +
            "SELECT lo_unlink(987662::oid);");

        DateTime cutoff;
        await using (var c = src.CreateCommand())
        {
            c.CommandText = "SELECT clock_timestamp()";
            cutoff = (DateTime)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        }

        await Task.Delay(1200, TestContext.Current.CancellationToken);
        await src.ExecAsync(
            "SELECT lo_put(987660::oid, 0, convert_to('cccccccccc', 'UTF8'));" +
            "SELECT lo_from_bytea(987663::oid, convert_to('after cutoff', 'UTF8'));");

        var log = Helpers.BackupPath("large_object_pitr");
        await src.BackupLogAsync(log, full);
        await src.CloseAsync();

        var target = "large_object_pitr_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full, log, cutoff);

            await using var conn = await _pg.ConnectToAsync(target);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT convert_from(lo_get(987660::oid), 'UTF8')";
                Assert.Equal("bbbbbbbbbb", (string)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT convert_from(lo_get(987661::oid), 'UTF8')";
                Assert.Equal("before cutoff", (string)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT EXISTS(SELECT 1 FROM pg_largeobject_metadata WHERE oid = 987662::oid)";
                Assert.False((bool)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT EXISTS(SELECT 1 FROM pg_largeobject_metadata WHERE oid = 987663::oid)";
                Assert.False((bool)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    private async Task RestoreAsync(string target, params string[] files)
    {
        await RestoreAsync(target, null, files);
    }

    private async Task RestoreAsync(string target, string full, string log,
        DateTime stopAt)
    {
        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbrestore(ARRAY[@full, @log]::text[], " +
            "target_db := @target, stop_at := @stop)";
        cmd.Parameters.AddWithValue("db", "ignored");
        cmd.Parameters.AddWithValue("full", full);
        cmd.Parameters.AddWithValue("log", log);
        cmd.Parameters.AddWithValue("target", target);
        cmd.Parameters.AddWithValue("stop", stopAt);
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        NpgsqlConnection.ClearAllPools();
    }

    private async Task RestoreAsync(string target, List<string>? notices,
        params string[] files)
    {
        await using var admin = await _pg.AdminAsync();
        if (notices is not null)
        {
            admin.Notice += (_, e) => notices.Add(e.Notice.MessageText);
        }

        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @target)";
        cmd.Parameters.AddWithValue("db", "ignored");
        cmd.Parameters.AddWithValue("files", files);
        cmd.Parameters.AddWithValue("target", target);
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        NpgsqlConnection.ClearAllPools();
    }
}
