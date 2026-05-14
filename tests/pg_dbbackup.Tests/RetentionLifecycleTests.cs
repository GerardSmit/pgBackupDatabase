using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Exercises set_retention_policy, pg_dbbackup_retention_plan, and
/// pg_dbbackup_apply_retention. Forces "aging" by rewriting
/// range_end_time on backup_artifacts so the test doesn't have to wait
/// real wall-clock time.
/// </summary>
[Collection(S3StorageCollection.Name)]
public sealed class RetentionLifecycleTests
{
    private readonly S3StorageFixture _fixture;

    public RetentionLifecycleTests(S3StorageFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task Retention_DryRun_Counts_Expired_Without_Marking_Deleted()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var bucket = "ret-" + Guid.NewGuid().ToString("N")[..10];
        var setName = "set_" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(conn, "minio", bucket, "p");
            await ConfigureSetAsync(conn, setName, db);

            await conn.ExecAsync(
                "CREATE TABLE t(id int PRIMARY KEY);" +
                "INSERT INTO t VALUES (1);");
            await BackupAsync(conn, setName, "full");

            // Policy: keep_fulls_for = 1 minute, keep_min_full_chains = 1.
            // The single backup is the only chain, so retention plan must
            // exclude it (keep_min_full_chains protects it).
            await SetPolicyAsync(conn, setName, "1 minute", "1 minute", 1);
            await AgeArtifactsAsync(conn, setName, "1 day");

            var planned = await ScalarLongAsync(conn,
                "SELECT count(*) FROM dbbackup.pg_dbbackup_retention_plan(@s)",
                setName);
            Assert.Equal(0L, planned);

            // Take a second FULL to create a new chain; now the first chain
            // can be retired (still 1 chain kept, the newest).
            await conn.ExecAsync("INSERT INTO t VALUES (2);");
            await BackupAsync(conn, setName, "full");
            await AgeArtifactsForChainAsync(conn, setName, chainIndex: 0, age: "1 day");

            planned = await ScalarLongAsync(conn,
                "SELECT count(*) FROM dbbackup.pg_dbbackup_retention_plan(@s)",
                setName);
            Assert.True(planned >= 1, $"expected >=1 expired row, got {planned}");

            var deletedDry = await ScalarIntAsync(conn,
                "SELECT dbbackup.pg_dbbackup_apply_retention(@s, true)",
                setName);
            Assert.Equal((int)planned, deletedDry);

            var actuallyDeleted = await ScalarLongAsync(conn,
                "SELECT count(*) FROM dbbackup.backup_artifacts " +
                "WHERE backup_set = @s AND status = 'deleted'",
                setName);
            Assert.Equal(0L, actuallyDeleted);
        }
        finally
        {
            await _fixture.DropDbAsync(db);
        }
    }

    [Fact]
    public async Task Retention_Apply_Marks_Artifacts_Deleted()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var bucket = "ret-" + Guid.NewGuid().ToString("N")[..10];
        var setName = "set_" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(conn, "minio", bucket, "p");
            await ConfigureSetAsync(conn, setName, db);
            await conn.ExecAsync(
                "CREATE TABLE t(id int PRIMARY KEY);" +
                "INSERT INTO t VALUES (1);");
            await BackupAsync(conn, setName, "full");
            await conn.ExecAsync("INSERT INTO t VALUES (2);");
            await BackupAsync(conn, setName, "full");

            await SetPolicyAsync(conn, setName, "1 minute", "1 minute", 1);
            await AgeArtifactsForChainAsync(conn, setName, chainIndex: 0, age: "10 days");

            var deleted = await ScalarIntAsync(conn,
                "SELECT dbbackup.pg_dbbackup_apply_retention(@s, false)",
                setName);
            Assert.True(deleted >= 1);

            var statusDeleted = await ScalarLongAsync(conn,
                "SELECT count(*) FROM dbbackup.backup_artifacts " +
                "WHERE backup_set = @s AND status = 'deleted'",
                setName);
            Assert.Equal((long)deleted, statusDeleted);

            // Retention event emitted.
            var events = await ScalarLongAsync(conn,
                "SELECT count(*) FROM dbbackup.backup_events " +
                "WHERE backup_set = @s AND event_type = 'retention_deleted'",
                setName);
            Assert.Equal((long)deleted, events);
        }
        finally
        {
            await _fixture.DropDbAsync(db);
        }
    }

    [Fact]
    public async Task SetRetentionPolicy_Upsert_Updates_In_Place()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var bucket = "ret-" + Guid.NewGuid().ToString("N")[..10];
        var setName = "set_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(conn, "minio", bucket, "p");
            await ConfigureSetAsync(conn, setName, db);

            await SetPolicyAsync(conn, setName, "3 days", "10 days", 4);
            await SetPolicyAsync(conn, setName, "7 days", "30 days", 2);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT keep_incrementals_for, keep_fulls_for, keep_min_full_chains " +
                "FROM dbbackup.retention_policies WHERE backup_set = @s";
            cmd.Parameters.AddWithValue("s", setName);
            await using var rdr = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(TimeSpan.FromDays(7), rdr.GetFieldValue<TimeSpan>(0));
            Assert.Equal(TimeSpan.FromDays(30), rdr.GetFieldValue<TimeSpan>(1));
            Assert.Equal(2, rdr.GetInt32(2));
        }
        finally
        {
            await _fixture.DropDbAsync(db);
        }
    }

    // ----- helpers -----

    private static async Task ConfigureSetAsync(Npgsql.NpgsqlConnection conn, string set, string db)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.create_backup_set(@s, 'minio'); " +
            "SELECT dbbackup.add_database_to_backup_set(@s, @d)";
        cmd.Parameters.AddWithValue("s", set);
        cmd.Parameters.AddWithValue("d", db);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task BackupAsync(Npgsql.NpgsqlConnection conn, string set, string type)
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

    private static async Task SetPolicyAsync(
        Npgsql.NpgsqlConnection conn, string set, string logs, string fulls, int minChains)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.set_retention_policy(" +
            "  backup_set := @s, " +
            "  keep_logs_for := @logs::interval, " +
            "  keep_fulls_for := @fulls::interval, " +
            "  keep_min_full_chains := @m)";
        cmd.Parameters.AddWithValue("s", set);
        cmd.Parameters.AddWithValue("logs", logs);
        cmd.Parameters.AddWithValue("fulls", fulls);
        cmd.Parameters.AddWithValue("m", minChains);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task AgeArtifactsAsync(
        Npgsql.NpgsqlConnection conn, string set, string age)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE dbbackup.backup_artifacts " +
            "SET range_end_time = now() - @age::interval, " +
            "    range_start_time = now() - @age::interval " +
            "WHERE backup_set = @s";
        cmd.Parameters.AddWithValue("s", set);
        cmd.Parameters.AddWithValue("age", age);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task AgeArtifactsForChainAsync(
        Npgsql.NpgsqlConnection conn, string set, int chainIndex, string age)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "WITH chains AS (" +
            "  SELECT chain_id, min(range_start_time) AS s " +
            "    FROM dbbackup.backup_artifacts " +
            "   WHERE backup_set = @s GROUP BY chain_id ORDER BY s" +
            ") " +
            "UPDATE dbbackup.backup_artifacts a " +
            "SET range_end_time = now() - @age::interval, " +
            "    range_start_time = now() - @age::interval " +
            "FROM (SELECT chain_id FROM chains LIMIT 1 OFFSET @idx) AS pick " +
            "WHERE a.backup_set = @s AND a.chain_id = pick.chain_id";
        cmd.Parameters.AddWithValue("s", set);
        cmd.Parameters.AddWithValue("idx", chainIndex);
        cmd.Parameters.AddWithValue("age", age);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> ScalarLongAsync(
        Npgsql.NpgsqlConnection conn, string sql, string set)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("s", set);
        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<int> ScalarIntAsync(
        Npgsql.NpgsqlConnection conn, string sql, string set)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("s", set);
        return (int)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
