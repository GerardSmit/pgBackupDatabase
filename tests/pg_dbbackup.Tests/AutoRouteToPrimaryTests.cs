using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Verifies the standby-to-primary auto-routing contract:
///   * pg_dbbackup_to_storage on a hot standby transparently re-issues the
///     call on the primary using primary_conninfo;
///   * pg_dbbackup (local-file output) is rejected on a standby because the
///     .bak would land on the primary's filesystem;
///   * primary_conninfo missing, the recursion guard, and remote errors all
///     surface to the caller with the right SQLSTATE.
/// </summary>
public sealed class AutoRouteToPrimaryTests : IAsyncLifetime
{
    private const string PostgresUser = "postgres";
    private const string PostgresPassword = "postgres";
    private const string PostgresDb = "postgres";
    private const string MinioImage = "minio/minio:latest";
    private const string AccessKey = "minioadmin";
    private const string SecretKey = "minioadmin";
    private const string StorageTargetName = "auto_route_target";
    private const string Bucket = "auto-route-bucket";

    private INetwork _network = null!;
    private IContainer _minio = null!;
    private IContainer _primary = null!;
    private IContainer _standby = null!;
    private readonly List<string> _createdDbs = new();

    public async ValueTask InitializeAsync()
    {
        var image = await CachedPgDbBackupImage.BuildAsync(Helpers.PostgresImage, Helpers.PgMajor);

        _network = new NetworkBuilder().Build();
        await _network.CreateAsync(TestContext.Current.CancellationToken);

        _minio = new ContainerBuilder(MinioImage)
            .WithEnvironment("MINIO_ROOT_USER", AccessKey)
            .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
            .WithCommand("server", "/data", "--address", ":9000")
            .WithNetwork(_network)
            .WithNetworkAliases("minio")
            .WithPortBinding(9000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(9000))
            .Build();
        await _minio.StartAsync(TestContext.Current.CancellationToken);

        _primary = BuildPrimary(image);
        await _primary.StartAsync(TestContext.Current.CancellationToken);
        await WaitForPostgresAsync(_primary, "primary");

        await using (var admin = await ConnectAsync(_primary, PostgresDb))
        {
            await ExecAsync(admin,
                "CREATE EXTENSION IF NOT EXISTS pg_dbbackup;" +
                "SELECT pg_create_physical_replication_slot('standby_slot');");
        }

        await ShellOrThrowAsync(_primary,
            "echo 'host replication all all trust' >> \"$PGDATA/pg_hba.conf\" && " +
            "su postgres -c \"pg_ctl -D '$PGDATA' reload\"",
            "enable replication host auth on primary");

        _standby = BuildStandby(image);
        await _standby.StartAsync(TestContext.Current.CancellationToken);
        await WaitForPostgresAsync(_standby, "standby");
        await WaitForStandbyReplicationActiveAsync();
    }

    public async ValueTask DisposeAsync()
    {
        // Drop the per-test databases before the container so the primary
        // stays clean on bare-metal CI where the container is reused.
        if (_primary is not null && _createdDbs.Count > 0)
        {
            try
            {
                await using var admin = await ConnectAsync(_primary, PostgresDb);
                foreach (var db in _createdDbs)
                {
                    try
                    {
                        await ExecAsync(admin,
                            $"DROP DATABASE IF EXISTS \"{db}\" WITH (FORCE)");
                    }
                    catch
                    {
                        // best-effort cleanup; container disposal will reclaim.
                    }
                }
            }
            catch
            {
                // best-effort cleanup; container disposal will reclaim.
            }
        }

        if (_standby is not null) await _standby.DisposeAsync();
        if (_primary is not null) await _primary.DisposeAsync();
        if (_minio is not null) await _minio.DisposeAsync();
        if (_network is not null) await _network.DisposeAsync();
    }

    [Fact]
    public async Task Storage_Full_Backup_On_Standby_Auto_Routes_To_Primary()
    {
        var dbName = await CreateChainDbAsync();
        await EnsureStorageTargetAsync(dbName);

        Guid backupId;
        await using (var fromStandby = await ConnectAsync(_standby, dbName))
        {
            Assert.True(await ScalarAsync<bool>(fromStandby, "SELECT pg_is_in_recovery()"));
            backupId = await StorageBackupAsync(fromStandby, dbName, "full", baseBackupId: null);
        }

        await using var onPrimary = await ConnectAsync(_primary, dbName);
        Assert.Equal(1L, await ScalarAsync<long>(onPrimary,
            "SELECT count(*) FROM dbbackup.backup_artifacts " +
            "WHERE backup_id = @id AND status = 'available'",
            ("id", backupId)));
        Assert.True(await ScalarAsync<bool>(onPrimary,
            "SELECT EXISTS (SELECT 1 FROM dbbackup.logical_chains " +
            "WHERE db_name = current_database() AND slot_name IS NOT NULL)"));

        await using var primaryAdmin = await ConnectAsync(_primary, PostgresDb);
        Assert.True(await ScalarAsync<bool>(primaryAdmin,
            "SELECT EXISTS (SELECT 1 FROM pg_replication_slots " +
            "WHERE slot_name LIKE '\\_pg\\_dbbackup\\_%')"));

        await using var standbyAdmin = await ConnectAsync(_standby, PostgresDb);
        // Replication slot was created on the primary; the standby has no
        // local copy (unless slot synchronization is on, which we did not
        // configure here).
        Assert.Equal(0L, await ScalarAsync<long>(standbyAdmin,
            "SELECT count(*) FROM pg_replication_slots " +
            "WHERE slot_name LIKE '\\_pg\\_dbbackup\\_%'"));
    }

    [Fact]
    public async Task Storage_Differential_Backup_On_Standby_Auto_Routes()
    {
        var dbName = await CreateChainDbAsync();
        await EnsureStorageTargetAsync(dbName);

        Guid fullId;
        await using (var onPrimary = await ConnectAsync(_primary, dbName))
        {
            fullId = await StorageBackupAsync(onPrimary, dbName, "full", baseBackupId: null);
            await ExecAsync(onPrimary,
                "INSERT INTO items VALUES (2, 'after-full');");
        }

        await WaitForStandbyToReplayAsync(dbName, 2);

        Guid diffId;
        await using (var fromStandby = await ConnectAsync(_standby, dbName))
        {
            diffId = await StorageBackupAsync(fromStandby, dbName, "differential",
                baseBackupId: fullId);
        }

        await using var onPrimary2 = await ConnectAsync(_primary, dbName);
        Assert.Equal(1L, await ScalarAsync<long>(onPrimary2,
            "SELECT count(*) FROM dbbackup.backup_artifacts " +
            "WHERE backup_id = @id AND backup_type = 'differential'",
            ("id", diffId)));
    }

    [Fact]
    public async Task Storage_Log_Backup_On_Standby_Auto_Routes()
    {
        var dbName = await CreateChainDbAsync();
        await EnsureStorageTargetAsync(dbName);

        Guid fullId;
        await using (var onPrimary = await ConnectAsync(_primary, dbName))
        {
            fullId = await StorageBackupAsync(onPrimary, dbName, "full", baseBackupId: null);
            await ExecAsync(onPrimary,
                "INSERT INTO items VALUES (3, 'before-log');");
        }

        await WaitForStandbyToReplayAsync(dbName, 2);

        Guid logId;
        await using (var fromStandby = await ConnectAsync(_standby, dbName))
        {
            logId = await StorageBackupAsync(fromStandby, dbName, "log",
                baseBackupId: fullId);
        }

        await using var onPrimary2 = await ConnectAsync(_primary, dbName);
        Assert.Equal(1L, await ScalarAsync<long>(onPrimary2,
            "SELECT count(*) FROM dbbackup.backup_artifacts " +
            "WHERE backup_id = @id AND backup_type = 'log'",
            ("id", logId)));
    }

    [Fact]
    public async Task LocalPath_Backup_On_Standby_Rejects_With_Explicit_Error()
    {
        var dbName = await CreateChainDbAsync();

        await using var fromStandby = await ConnectAsync(_standby, dbName);
        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = fromStandby.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbbackup(@db, @path, type := 'full', " +
                "compress := false)";
            cmd.Parameters.AddWithValue("db", dbName);
            cmd.Parameters.AddWithValue("path", $"/tmp/{dbName}_local.bak");
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        });

        Assert.Equal("25006", ex.SqlState);
        var text = ex.ToString();
        Assert.Contains("standby", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pg_dbbackup_to_storage", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Storage_Backup_On_Standby_Missing_Primary_Conninfo_Errors()
    {
        var dbName = await CreateChainDbAsync();
        await EnsureStorageTargetAsync(dbName);

        string? originalConninfo = null;
        await using (var standbyAdmin = await ConnectAsync(_standby, PostgresDb))
        {
            originalConninfo = await ScalarAsync<string>(standbyAdmin,
                "SELECT current_setting('primary_conninfo', false)");
        }

        try
        {
            await SetPrimaryConninfoOnStandbyAsync(string.Empty);

            await using var fromStandby = await ConnectAsync(_standby, dbName);
            var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
                await StorageBackupAsync(fromStandby, dbName, "full", baseBackupId: null));

            Assert.Equal("55000", ex.SqlState);
            Assert.Contains("primary_conninfo", ex.ToString(),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (originalConninfo is not null)
                await SetPrimaryConninfoOnStandbyAsync(originalConninfo);
        }
    }

    [Fact]
    public async Task Storage_Backup_On_Standby_Recursion_Guard_Triggers()
    {
        var dbName = await CreateChainDbAsync();
        await EnsureStorageTargetAsync(dbName);

        await using var fromStandby = await ConnectAsync(_standby, dbName);
        await ExecAsync(fromStandby, "SET dbbackup.in_remote_invocation = on");

        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            await StorageBackupAsync(fromStandby, dbName, "full", baseBackupId: null));

        Assert.Equal("55000", ex.SqlState);
        Assert.Contains("still in recovery", ex.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Storage_Backup_On_Standby_Propagates_Remote_Error()
    {
        var dbName = await CreateChainDbAsync();

        await using var fromStandby = await ConnectAsync(_standby, dbName);
        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = fromStandby.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbbackup_to_storage(@db, @type, @target)";
            cmd.Parameters.AddWithValue("db", dbName);
            cmd.Parameters.AddWithValue("type", "full");
            cmd.Parameters.AddWithValue("target", "does_not_exist");
            cmd.CommandTimeout = 60;
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        });

        // Forwarded from the primary; missing target name lands in
        // MessageText (the upstream primary errmsg, preserved verbatim by
        // the auto-route).
        Assert.Contains("does_not_exist", ex.MessageText,
            StringComparison.OrdinalIgnoreCase);
        // load_s3_target raises ERRCODE_UNDEFINED_OBJECT (42704); accept
        // adjacent codes so the test does not pin to one resolver path.
        Assert.True(ex.SqlState is "42704" or "55000" or "22023",
            $"unexpected SqlState {ex.SqlState}: {ex}");
    }

    private async Task<string> CreateChainDbAsync()
    {
        var dbName = "auto_route_" + Guid.NewGuid().ToString("N")[..8];
        _createdDbs.Add(dbName);

        await using (var admin = await ConnectAsync(_primary, PostgresDb))
        {
            await ExecAsync(admin, $"CREATE DATABASE \"{dbName}\"");
        }

        await using (var source = await ConnectAsync(_primary, dbName))
        {
            await ExecAsync(source,
                "CREATE EXTENSION pg_dbbackup;" +
                "SELECT dbbackup.pg_dbbackup_set_mode(current_database(), 'full');" +
                "CREATE TABLE items(id int PRIMARY KEY, v text);" +
                "INSERT INTO items VALUES (1, 'base');");
        }

        await WaitForStandbyToReplayAsync(dbName, 1);
        return dbName;
    }

    private async Task EnsureStorageTargetAsync(string dbName)
    {
        await using var conn = await ConnectAsync(_primary, dbName);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT dbbackup.create_s3_target(" +
                "  name := @name, bucket := @bucket, prefix := '', " +
                "  region := 'us-east-1', endpoint_url := 'http://minio:9000', " +
                "  force_path_style := true)";
            cmd.Parameters.AddWithValue("name", StorageTargetName);
            cmd.Parameters.AddWithValue("bucket", Bucket);
            try
            {
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            catch (PostgresException pe) when (pe.SqlState == "23505")
            {
                // already exists from a previous test in the class — fine.
            }
        }

        for (var i = 0; i < 20; i++)
        {
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT dbbackup.pg_dbbackup_s3_create_bucket(@n)";
                cmd.Parameters.AddWithValue("n", StorageTargetName);
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                return;
            }
            catch
            {
                await Task.Delay(500, TestContext.Current.CancellationToken);
            }
        }
        throw new InvalidOperationException("could not create MinIO bucket");
    }

    private async Task<Guid> StorageBackupAsync(
        NpgsqlConnection conn,
        string dbName,
        string type,
        Guid? baseBackupId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbbackup_to_storage(" +
            "  dbname := @db, type := @type, " +
            "  storage_target := @target, compress := false, " +
            "  base_backup_id := @base)";
        cmd.Parameters.AddWithValue("db", dbName);
        cmd.Parameters.AddWithValue("type", type);
        cmd.Parameters.AddWithValue("target", StorageTargetName);
        var baseParam = cmd.Parameters.Add("base", NpgsqlTypes.NpgsqlDbType.Uuid);
        baseParam.Value = baseBackupId.HasValue ? baseBackupId.Value : DBNull.Value;
        cmd.CommandTimeout = 240;
        var result = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return (Guid)result!;
    }

    private async Task SetPrimaryConninfoOnStandbyAsync(string conninfo)
    {
        await using var standbyAdmin = await ConnectAsync(_standby, PostgresDb);
        await using (var cmd = standbyAdmin.CreateCommand())
        {
            // ALTER SYSTEM literals don't accept parameters, so escape inline.
            var literal = "'" + conninfo.Replace("'", "''") + "'";
            cmd.CommandText = $"ALTER SYSTEM SET primary_conninfo = {literal}";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using (var cmd = standbyAdmin.CreateCommand())
        {
            cmd.CommandText = "SELECT pg_reload_conf()";
            await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        // primary_conninfo is PGC_SIGHUP; backends observe the new value after
        // reload completes. Poll until it matches what we requested so the
        // following call sees the intended state.
        for (var i = 0; i < 40; i++)
        {
            await using var probe = await ConnectAsync(_standby, PostgresDb);
            var current = await ScalarAsync<string>(probe,
                "SELECT current_setting('primary_conninfo', false)");
            if (string.Equals(current ?? string.Empty, conninfo, StringComparison.Ordinal))
                return;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        throw new InvalidOperationException(
            $"primary_conninfo on standby did not converge to expected value");
    }

    private async Task WaitForStandbyToReplayAsync(string dbName, long expectedItems)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var replica = await ConnectAsync(_standby, dbName);
                if (await ScalarAsync<long>(replica, "SELECT count(*) FROM items") >= expectedItems)
                    return;
            }
            catch
            {
                // database may not yet exist on the standby — keep waiting.
            }
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException(
            $"standby did not replay {expectedItems} rows for {dbName} in time");
    }

    private async Task WaitForStandbyReplicationActiveAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            await using var admin = await ConnectAsync(_primary, PostgresDb);
            if (await ScalarAsync<bool>(admin,
                    "SELECT COALESCE((SELECT active FROM pg_replication_slots " +
                    "WHERE slot_name = 'standby_slot'), false)"))
                return;
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("standby_slot did not become active");
    }

    private IContainer BuildPrimary(string image) =>
        new ContainerBuilder(image)
            .WithNetwork(_network)
            .WithNetworkAliases("pg-primary")
            .WithPortBinding(5432, true)
            .WithEnvironment("POSTGRES_USER", PostgresUser)
            .WithEnvironment("POSTGRES_PASSWORD", PostgresPassword)
            .WithEnvironment("POSTGRES_DB", PostgresDb)
            .WithEnvironment("POSTGRES_HOST_AUTH_METHOD", "trust")
            .WithEnvironment("AWS_ACCESS_KEY_ID", AccessKey)
            .WithEnvironment("AWS_SECRET_ACCESS_KEY", SecretKey)
            .WithEnvironment("AWS_REGION", "us-east-1")
            .WithCommand(
                "postgres",
                "-c", "shared_preload_libraries=pg_dbbackup",
                "-c", "wal_level=logical",
                "-c", "max_replication_slots=20",
                "-c", "max_wal_senders=20",
                "-c", "track_commit_timestamp=on",
                "-c", "wal_keep_size=128",
                "-c", "listen_addresses=*")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .AddCustomWaitStrategy(new ImmediateReadyWait()))
            .Build();

    private IContainer BuildStandby(string image)
    {
        var command =
            "set -e; " +
            "mkdir -p \"$PGDATA\"; " +
            "rm -rf \"$PGDATA\"/*; " +
            "chown -R postgres:postgres \"$PGDATA\"; " +
            "until pg_isready -h pg-primary -U postgres -d postgres; do sleep 1; done; " +
            "su postgres -c \"PGPASSWORD=postgres pg_basebackup " +
            "-h pg-primary -U postgres -D '$PGDATA' -Fp -Xs -R -S standby_slot\"; " +
            "cat >> \"$PGDATA/postgresql.auto.conf\" <<'EOF'\n" +
            "hot_standby_feedback = on\n" +
            "primary_slot_name = 'standby_slot'\n" +
            "primary_conninfo = 'host=pg-primary port=5432 user=postgres password=postgres dbname=postgres application_name=standby'\n" +
            "EOF\n" +
            "chown -R postgres:postgres \"$PGDATA\"; " +
            "chmod 700 \"$PGDATA\"; " +
            "bindir=$(pg_config --bindir); " +
            "exec su postgres -c \"$bindir/postgres -D '$PGDATA' " +
            "-c shared_preload_libraries=pg_dbbackup " +
            "-c wal_level=logical " +
            "-c max_replication_slots=20 " +
            "-c max_wal_senders=20 " +
            "-c track_commit_timestamp=on " +
            "-c wal_keep_size=128 " +
            "-c listen_addresses=*\"";

        return new ContainerBuilder(image)
            .WithNetwork(_network)
            .WithNetworkAliases("pg-standby")
            .WithPortBinding(5432, true)
            .WithEnvironment("POSTGRES_USER", PostgresUser)
            .WithEnvironment("POSTGRES_PASSWORD", PostgresPassword)
            .WithEnvironment("POSTGRES_DB", PostgresDb)
            .WithEnvironment("POSTGRES_HOST_AUTH_METHOD", "trust")
            .WithEnvironment("AWS_ACCESS_KEY_ID", AccessKey)
            .WithEnvironment("AWS_SECRET_ACCESS_KEY", SecretKey)
            .WithEnvironment("AWS_REGION", "us-east-1")
            .WithCommand("bash", "-lc", command)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .AddCustomWaitStrategy(new ImmediateReadyWait()))
            .Build();
    }

    private static string PostgresConnectionString(IContainer container, string database) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Port = container.GetMappedPublicPort(5432),
            Username = PostgresUser,
            Password = PostgresPassword,
            Database = database,
            Pooling = false,
            Timeout = 5,
        }.ConnectionString;

    private static async Task<NpgsqlConnection> ConnectAsync(IContainer container, string database)
    {
        var conn = new NpgsqlConnection(PostgresConnectionString(container, database));
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        return conn;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 240;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection conn,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 240;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return (T)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task WaitForPostgresAsync(IContainer container, string label)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var conn = new NpgsqlConnection(
                    PostgresConnectionString(container, PostgresDb));
                await conn.OpenAsync(TestContext.Current.CancellationToken);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1";
                await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
                return;
            }
            catch (Exception e)
            {
                last = e;
                await Task.Delay(500, TestContext.Current.CancellationToken);
            }
        }
        throw new InvalidOperationException(
            $"PostgreSQL {label} not ready in time", last);
    }

    private static async Task ShellOrThrowAsync(
        IContainer container,
        string command,
        string label)
    {
        var result = await container.ExecAsync(
            new[] { "sh", "-c", command },
            TestContext.Current.CancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"{label} failed: stdout={result.Stdout} stderr={result.Stderr}");
    }
}
