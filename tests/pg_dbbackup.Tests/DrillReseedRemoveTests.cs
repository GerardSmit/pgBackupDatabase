using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Coverage for the storage-set operational helpers:
/// restore_drill / restore_drill_plan, pg_dbbackup_reseed_if_needed,
/// remove_database_from_backup_set, and pg_dbrestore_at.
/// </summary>
[Collection(S3StorageCollection.Name)]
public sealed class DrillReseedRemoveTests
{
    private readonly S3StorageFixture _fixture;

    public DrillReseedRemoveTests(S3StorageFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task RestoreDrill_Records_Event_And_Creates_Target()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var setName = "drill_" + Guid.NewGuid().ToString("N")[..8];
        var bucket = "drill-" + Guid.NewGuid().ToString("N")[..10];
        var target = "drill_t_" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(conn, "minio", bucket, "p");
            await ConfigureSetAsync(conn, setName, db);
            await conn.SetModeFullAsync();
            await conn.ExecAsync(
                "CREATE TABLE drill_t(id int PRIMARY KEY, v text);" +
                "INSERT INTO drill_t VALUES (1, 'one');");
            await BackupAsync(conn, setName, "full");
            await conn.ExecAsync("INSERT INTO drill_t VALUES (2, 'two');");
            await BackupAsync(conn, setName, "log");

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.restore_drill(" +
                    "  dbname := @db, storage_target := 'minio', " +
                    "  stop_at := now() + interval '1 hour', target_db := @tgt)";
                cmd.Parameters.AddWithValue("db", db);
                cmd.Parameters.AddWithValue("tgt", target);
                cmd.CommandTimeout = 120;
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var events = await ScalarLongAsync(conn,
                "SELECT count(*) FROM dbbackup.backup_events " +
                "WHERE event_type = 'restore_drill_completed' AND dbname = @db",
                ("db", db));
            Assert.Equal(1L, events);

            await using var restored = await _fixture.ConnectToAsync(target);
            await using var verify = restored.CreateCommand();
            verify.CommandText = "SELECT count(*) FROM drill_t";
            Assert.Equal(2L, (long)(await verify.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally
        {
            await _fixture.DropDbAsync(target);
            await _fixture.DropDbAsync(db);
        }
    }

    [Fact]
    public async Task RestoreDrillPlan_Returns_Active_Members()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var setName = "plan_" + Guid.NewGuid().ToString("N")[..8];
        var bucket = "plan-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(conn, "minio", bucket, "p");
            await ConfigureSetAsync(conn, setName, db);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT dbname, storage_target " +
                "FROM dbbackup.restore_drill_plan(@s, 3)";
            cmd.Parameters.AddWithValue("s", setName);
            await using var rdr = await cmd.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(db, rdr.GetString(0));
            Assert.Equal("minio", rdr.GetString(1));
        }
        finally
        {
            await _fixture.DropDbAsync(db);
        }
    }

    [Fact]
    public async Task PgDbrestoreAt_Restores_Latest_Chain_Tip()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var setName = "atfn_" + Guid.NewGuid().ToString("N")[..8];
        var bucket = "atfn-" + Guid.NewGuid().ToString("N")[..10];
        var target = "atfn_t_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(conn, "minio", bucket, "p");
            await ConfigureSetAsync(conn, setName, db);
            await conn.SetModeFullAsync();
            await conn.ExecAsync(
                "CREATE TABLE at_t(id int PRIMARY KEY);" +
                "INSERT INTO at_t VALUES (1);");
            await BackupAsync(conn, setName, "full");
            await conn.ExecAsync("INSERT INTO at_t VALUES (2);");
            await BackupAsync(conn, setName, "log");

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbrestore_at(" +
                    "  dbname := @db, target_db := @tgt, " +
                    "  stop_at := now() + interval '1 hour', " +
                    "  storage_target := 'minio')";
                cmd.Parameters.AddWithValue("db", db);
                cmd.Parameters.AddWithValue("tgt", target);
                cmd.CommandTimeout = 120;
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await using var restored = await _fixture.ConnectToAsync(target);
            await using var verify = restored.CreateCommand();
            verify.CommandText = "SELECT count(*) FROM at_t";
            Assert.Equal(2L, (long)(await verify.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally
        {
            await _fixture.DropDbAsync(target);
            await _fixture.DropDbAsync(db);
        }
    }

    [Fact]
    public async Task ReseedIfNeeded_FullNow_Takes_Fresh_Full_When_Slot_Unsafe()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var setName = "reseed_" + Guid.NewGuid().ToString("N")[..8];
        var bucket = "reseed-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(conn, "minio", bucket, "p");
            await ConfigureSetAsync(conn, setName, db, "full_now");
            await conn.SetModeFullAsync();
            await conn.ExecAsync(
                "CREATE TABLE r_t(id int PRIMARY KEY);" +
                "INSERT INTO r_t VALUES (1);");
            await BackupAsync(conn, setName, "full");

            // Simulate unsafe slot by dropping it.
            await conn.ExecAsync(
                "SELECT pg_drop_replication_slot('_pg_dbbackup_' || " +
                "(SELECT oid::text FROM pg_database WHERE datname = current_database()))");

            var made = await ScalarIntAsync(conn,
                "SELECT dbbackup.pg_dbbackup_reseed_if_needed(@s)",
                setName);
            Assert.Equal(1, made);

            // Verify a fresh FULL artifact appeared.
            var fullCount = await ScalarLongAsync(conn,
                "SELECT count(*) FROM dbbackup.backup_artifacts " +
                "WHERE backup_set = @s AND backup_type = 'full'",
                ("s", setName));
            Assert.Equal(2L, fullCount);
        }
        finally
        {
            await _fixture.DropDbAsync(db);
        }
    }

    [Fact]
    public async Task ReseedIfNeeded_WarnOnly_Emits_Event_Without_Backup()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var setName = "warn_" + Guid.NewGuid().ToString("N")[..8];
        var bucket = "warn-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(conn, "minio", bucket, "p");
            await ConfigureSetAsync(conn, setName, db, "warn_only");
            await conn.SetModeFullAsync();
            await conn.ExecAsync(
                "CREATE TABLE w_t(id int PRIMARY KEY);" +
                "INSERT INTO w_t VALUES (1);");
            await BackupAsync(conn, setName, "full");
            await conn.ExecAsync(
                "SELECT pg_drop_replication_slot('_pg_dbbackup_' || " +
                "(SELECT oid::text FROM pg_database WHERE datname = current_database()))");

            var made = await ScalarIntAsync(conn,
                "SELECT dbbackup.pg_dbbackup_reseed_if_needed(@s)",
                setName);
            Assert.Equal(0, made);

            var warnings = await ScalarLongAsync(conn,
                "SELECT count(*) FROM dbbackup.backup_events " +
                "WHERE backup_set = @s AND event_type = 'unsafe_failover_slot'",
                ("s", setName));
            Assert.Equal(1L, warnings);
        }
        finally
        {
            await _fixture.DropDbAsync(db);
        }
    }

    [Fact]
    public async Task RemoveDatabase_KeepHistory_Marks_Inactive()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var setName = "rmh_" + Guid.NewGuid().ToString("N")[..8];
        var bucket = "rmh-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(conn, "minio", bucket, "p");
            await ConfigureSetAsync(conn, setName, db);

            // close_chain false (database is simple-mode, no chain), keep_history true.
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.remove_database_from_backup_set(@s, @d, false, true)";
                cmd.Parameters.AddWithValue("s", setName);
                cmd.Parameters.AddWithValue("d", db);
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await using var cmd2 = conn.CreateCommand();
            cmd2.CommandText =
                "SELECT active, removed_at IS NOT NULL " +
                "FROM dbbackup.backup_set_databases " +
                "WHERE backup_set = @s AND dbname = @d";
            cmd2.Parameters.AddWithValue("s", setName);
            cmd2.Parameters.AddWithValue("d", db);
            await using var rdr = await cmd2.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
            Assert.False(rdr.GetBoolean(0));
            Assert.True(rdr.GetBoolean(1));
        }
        finally
        {
            await _fixture.DropDbAsync(db);
        }
    }

    [Fact]
    public async Task RemoveDatabase_NoHistory_Deletes_Row()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var setName = "rmn_" + Guid.NewGuid().ToString("N")[..8];
        var bucket = "rmn-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(conn, "minio", bucket, "p");
            await ConfigureSetAsync(conn, setName, db);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.remove_database_from_backup_set(@s, @d, false, false)";
                cmd.Parameters.AddWithValue("s", setName);
                cmd.Parameters.AddWithValue("d", db);
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var remaining = await ScalarLongAsync(conn,
                "SELECT count(*) FROM dbbackup.backup_set_databases " +
                "WHERE backup_set = @s AND dbname = @d",
                ("s", setName), ("d", db));
            Assert.Equal(0L, remaining);
        }
        finally
        {
            await _fixture.DropDbAsync(db);
        }
    }

    [Fact]
    public async Task RemoveDatabase_MissingSet_Raises()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        try
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT dbbackup.remove_database_from_backup_set('no_such_set', @d)";
                cmd.Parameters.AddWithValue("d", db);
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            });
            Assert.Contains("does not exist", ex.MessageText);
        }
        finally
        {
            await _fixture.DropDbAsync(db);
        }
    }

    // ----- helpers -----

    private static async Task ConfigureSetAsync(
        NpgsqlConnection conn, string set, string db,
        string onUnsafeFailover = "full_now")
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.create_backup_set(@s, 'minio', @policy); " +
            "SELECT dbbackup.add_database_to_backup_set(@s, @d)";
        cmd.Parameters.AddWithValue("s", set);
        cmd.Parameters.AddWithValue("d", db);
        cmd.Parameters.AddWithValue("policy", onUnsafeFailover);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task BackupAsync(NpgsqlConnection conn, string set, string type)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbbackup_to_storage(" +
            "  dbname := @d, type := @t, storage_target := 'minio', " +
            "  backup_set := @s, compress := false)";
        cmd.Parameters.AddWithValue("d", conn.Database!);
        cmd.Parameters.AddWithValue("t", type);
        cmd.Parameters.AddWithValue("s", set);
        cmd.CommandTimeout = 120;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> ScalarLongAsync(
        NpgsqlConnection conn, string sql, params (string, object)[] pars)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in pars) cmd.Parameters.AddWithValue(n, v);
        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<int> ScalarIntAsync(
        NpgsqlConnection conn, string sql, string setParam)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("s", setParam);
        cmd.CommandTimeout = 120;
        return (int)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
