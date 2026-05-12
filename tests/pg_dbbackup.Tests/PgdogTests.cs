using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class PgdogTests
{
    private const string PgdogImage = "ghcr.io/pgdogdev/pgdog:latest";
    private const string PostgresUser = "postgres";
    private const string PostgresPassword = "postgres";
    private const string PostgresDb = "postgres";

    [Fact]
    public async Task Pgdog_Backup_Primary_Route_Succeeds_Replica_Route_Errors()
    {
        var image = await CachedPgDbBackupImage.BuildAsync(Helpers.PostgresImage, Helpers.PgMajor);
        var dbName = "pgdog_" + Guid.NewGuid().ToString("N")[..8];
        var fullPath = $"/tmp/{dbName}_full.bak";
        var primaryPgdogConfigDir = Path.Combine(
            Path.GetTempPath(),
            $"pgdbbackup_pgdog_primary_{Guid.NewGuid():N}");
        var standbyPgdogConfigDir = Path.Combine(
            Path.GetTempPath(),
            $"pgdbbackup_pgdog_standby_{Guid.NewGuid():N}");

        await using var network = new NetworkBuilder().Build();
        await network.CreateAsync(TestContext.Current.CancellationToken);

        IContainer? primary = null;
        IContainer? standby = null;
        IContainer? primaryPgdog = null;
        IContainer? standbyPgdog = null;

        try
        {
            primary = BuildPrimary(image, network);
            await primary.StartAsync(TestContext.Current.CancellationToken);
            await WaitForReadyConnectionAsync(primary, "primary");

            await using (var admin = await ConnectAsync(primary, PostgresDb))
            {
                await ExecSqlAsync(admin,
                    "CREATE EXTENSION IF NOT EXISTS pg_dbbackup;" +
                    "SELECT pg_create_physical_replication_slot('standby_slot');");
            }

            await ShellOrThrowAsync(primary,
                "echo 'host replication all all trust' >> \"$PGDATA/pg_hba.conf\" && " +
                "su postgres -c \"pg_ctl -D '$PGDATA' reload\"",
                "enable replication host auth");

            standby = BuildStandby(image, network);
            await standby.StartAsync(TestContext.Current.CancellationToken);
            await WaitForReadyConnectionAsync(standby, "standby");
            await WaitForConditionAsync(async () =>
            {
                await using var admin = await ConnectAsync(primary, PostgresDb);
                return await ScalarAsync<bool>(admin,
                    "SELECT COALESCE((" +
                    "  SELECT active FROM pg_replication_slots " +
                    "  WHERE slot_name = 'standby_slot'), false)");
            }, "standby physical replication slot to become active");

            await using (var admin = await ConnectAsync(primary, PostgresDb))
            {
                await ExecSqlAsync(admin, $"CREATE DATABASE \"{dbName}\"");
            }

            await using (var source = await ConnectAsync(primary, dbName))
            {
                await ExecSqlAsync(source,
                    "CREATE EXTENSION pg_dbbackup;" +
                    "SELECT dbbackup.pg_dbbackup_set_mode(current_database(), 'full');" +
                    "CREATE TABLE items(id int PRIMARY KEY, v text);" +
                    "INSERT INTO items VALUES (1, 'base');");
            }

            await WaitForConditionAsync(async () =>
            {
                try
                {
                    await using var replica = await ConnectAsync(standby, dbName);
                    return await ScalarAsync<long>(replica, "SELECT count(*) FROM items") == 1;
                }
                catch
                {
                    return false;
                }
            }, "standby to replay source database");

            primaryPgdog = BuildPgdog(
                network,
                backendAlias: "pg-primary",
                backendRole: "primary",
                dbName,
                routeAlias: "pgdog-primary-route",
                configDir: primaryPgdogConfigDir);
            await primaryPgdog.StartAsync(TestContext.Current.CancellationToken);
            await WaitForPgdogConnectionAsync(primaryPgdog, dbName, "primary route");

            await using (var throughPrimary = await ConnectPgdogAsync(primaryPgdog, dbName))
            {
                Assert.False(await ScalarAsync<bool>(
                    throughPrimary,
                    "SELECT pg_is_in_recovery()"));

                await BackupAsync(throughPrimary, "full", fullPath, null);
            }

            await ShellOrThrowAsync(primary,
                $"test -s '{fullPath}'",
                "primary-route pgdog backup output exists");

            await using (var source = await ConnectAsync(primary, dbName))
            {
                Assert.True(await ScalarAsync<bool>(source,
                    "SELECT EXISTS (" +
                    "  SELECT 1 FROM dbbackup.logical_chains " +
                    "  WHERE db_name = current_database() AND slot_name IS NOT NULL)"));
            }

            await using (var directStandby = await ConnectAsync(standby, dbName))
            {
                Assert.True(await ScalarAsync<bool>(
                    directStandby,
                    "SELECT pg_is_in_recovery()"));

                var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
                    await BackupAsync(directStandby, "full", $"/tmp/{dbName}_direct_standby.bak", null));
                AssertStandbyBackupError(ex);
            }

            standbyPgdog = BuildPgdog(
                network,
                backendAlias: "pg-standby",
                backendRole: "replica",
                dbName,
                routeAlias: "pgdog-standby-route",
                configDir: standbyPgdogConfigDir);
            await standbyPgdog.StartAsync(TestContext.Current.CancellationToken);

            var pgdogEx = await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await using var throughStandby = await ConnectPgdogAsync(standbyPgdog, dbName);
                await BackupAsync(throughStandby, "full", $"/tmp/{dbName}_pgdog_standby.bak", null);
            });
            AssertPgdogStandbyRouteError(pgdogEx);
        }
        finally
        {
            if (standbyPgdog is not null)
                await standbyPgdog.DisposeAsync();
            if (primaryPgdog is not null)
                await primaryPgdog.DisposeAsync();
            if (standby is not null)
                await standby.DisposeAsync();
            if (primary is not null)
                await primary.DisposeAsync();

            DeleteDirectoryQuietly(primaryPgdogConfigDir);
            DeleteDirectoryQuietly(standbyPgdogConfigDir);
        }
    }

    private static IContainer BuildPrimary(string image, INetwork network) =>
        new ContainerBuilder(image)
            .WithNetwork(network)
            .WithNetworkAliases("pg-primary")
            .WithPortBinding(5432, true)
            .WithEnvironment("POSTGRES_USER", PostgresUser)
            .WithEnvironment("POSTGRES_PASSWORD", PostgresPassword)
            .WithEnvironment("POSTGRES_DB", PostgresDb)
            .WithEnvironment("POSTGRES_HOST_AUTH_METHOD", "trust")
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

    private static IContainer BuildStandby(string image, INetwork network)
    {
        var command =
            "set -e; " +
            "rm -rf \"$PGDATA\"/*; " +
            "chown -R postgres:postgres \"$PGDATA\"; " +
            "until pg_isready -h pg-primary -U postgres -d postgres; do sleep 1; done; " +
            "su postgres -c \"PGPASSWORD=postgres pg_basebackup " +
            "-h pg-primary -U postgres -D '$PGDATA' -Fp -Xs -R -S standby_slot\"; " +
            "cat >> \"$PGDATA/postgresql.auto.conf\" <<'EOF'\n" +
            "hot_standby_feedback = on\n" +
            "sync_replication_slots = on\n" +
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
            .WithNetwork(network)
            .WithNetworkAliases("pg-standby")
            .WithPortBinding(5432, true)
            .WithEnvironment("POSTGRES_USER", PostgresUser)
            .WithEnvironment("POSTGRES_PASSWORD", PostgresPassword)
            .WithEnvironment("POSTGRES_DB", PostgresDb)
            .WithEnvironment("POSTGRES_HOST_AUTH_METHOD", "trust")
            .WithCommand("bash", "-lc", command)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .AddCustomWaitStrategy(new ImmediateReadyWait()))
            .Build();
    }

    private static IContainer BuildPgdog(
        INetwork network,
        string backendAlias,
        string backendRole,
        string dbName,
        string routeAlias,
        string configDir)
    {
        Directory.CreateDirectory(configDir);
        File.WriteAllText(
            Path.Combine(configDir, "pgdog.toml"),
            PgdogConfig(backendAlias, backendRole, dbName),
            Encoding.ASCII);
        File.WriteAllText(
            Path.Combine(configDir, "users.toml"),
            PgdogUsers(dbName),
            Encoding.ASCII);

        return new ContainerBuilder(PgdogImage)
            .WithNetwork(network)
            .WithNetworkAliases(routeAlias)
            .WithPortBinding(6432, true)
            .WithBindMount(configDir, "/config", AccessMode.ReadOnly)
            .WithCommand(
                "pgdog",
                "-c", "/config/pgdog.toml",
                "-u", "/config/users.toml",
                "run")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .AddCustomWaitStrategy(new ImmediateReadyWait()))
            .Build();
    }

    private static string PgdogConfig(string backendAlias, string backendRole, string dbName) =>
        $"""
        [general]
        port = 6432
        default_pool_size = 2
        min_pool_size = 0

        [[databases]]
        name = "{dbName}"
        host = "{backendAlias}"
        port = 5432
        database_name = "{dbName}"
        role = "{backendRole}"
        """;

    private static string PgdogUsers(string dbName) =>
        $"""
        [[users]]
        name = "{PostgresUser}"
        database = "{dbName}"
        password = "{PostgresPassword}"
        """;

    private static string PostgresConnectionString(IContainer container, string database = PostgresDb) =>
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

    private static string PgdogConnectionString(IContainer container, string database) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Port = container.GetMappedPublicPort(6432),
            Username = PostgresUser,
            Password = PostgresPassword,
            Database = database,
            Pooling = false,
            Timeout = 5,
        }.ConnectionString;

    private static async Task<NpgsqlConnection> ConnectAsync(
        IContainer container,
        string database)
    {
        var conn = new NpgsqlConnection(PostgresConnectionString(container, database));
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        return conn;
    }

    private static async Task<NpgsqlConnection> ConnectPgdogAsync(
        IContainer container,
        string database)
    {
        var conn = new NpgsqlConnection(PgdogConnectionString(container, database));
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        return conn;
    }

    private static async Task WaitForReadyConnectionAsync(
        IContainer container,
        string label)
    {
        var connectionString = PostgresConnectionString(container);
        var deadline = DateTime.UtcNow.AddSeconds(120);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
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
            $"PostgreSQL {label} was not ready in time{await LogsAsync(container)}",
            last);
    }

    private static async Task WaitForPgdogConnectionAsync(
        IContainer container,
        string database,
        string label)
    {
        var connectionString = PgdogConnectionString(container, database);
        var deadline = DateTime.UtcNow.AddSeconds(90);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
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
            $"Pgdog {label} was not ready in time{await LogsAsync(container)}",
            last);
    }

    private static async Task WaitForConditionAsync(
        Func<Task<bool>> predicate,
        string label,
        int timeoutSeconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate())
                return;
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for {label}");
    }

    private static async Task ExecSqlAsync(NpgsqlConnection conn, string sql)
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

    private static async Task BackupAsync(
        NpgsqlConnection conn,
        string type,
        string path,
        string? basePath)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbbackup(@db, @path, type := @type, " +
            "compress := false, base_filepath := @base)";
        cmd.Parameters.AddWithValue("db", conn.Database!);
        cmd.Parameters.AddWithValue("path", path);
        cmd.Parameters.AddWithValue("type", type);
        cmd.Parameters.AddWithValue("base", (object?)basePath ?? DBNull.Value);
        cmd.CommandTimeout = 240;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static void AssertStandbyBackupError(Exception ex)
    {
        var message = ex.ToString();
        Assert.True(
            ContainsIgnoreCase(message, "read-only") ||
            ContainsIgnoreCase(message, "recovery") ||
            ContainsIgnoreCase(message, "standby") ||
            ContainsIgnoreCase(message, "cannot execute"),
            "Expected backup through a standby route to fail with a standby/read-only " +
            $"error, but got: {message}");
    }

    private static void AssertPgdogStandbyRouteError(Exception ex)
    {
        var message = ex.ToString();
        Assert.True(
            ContainsIgnoreCase(message, "read-only") ||
            ContainsIgnoreCase(message, "recovery") ||
            ContainsIgnoreCase(message, "standby") ||
            ContainsIgnoreCase(message, "connection pool") ||
            ContainsIgnoreCase(message, "pool is down"),
            "Expected pgdog standby route to fail instead of routing backup to " +
            $"primary, but got: {message}");
    }

    private static bool ContainsIgnoreCase(string value, string expected) =>
        value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;

    private static async Task ShellOrThrowAsync(
        IContainer container,
        string command,
        string label)
    {
        var result = await container.ExecAsync(new[] { "sh", "-c", command });
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"{label} failed: stdout={result.Stdout} stderr={result.Stderr}");
    }

    private static async Task<string> LogsAsync(IContainer container)
    {
        try
        {
            var (stdout, stderr) = await container.GetLogsAsync();
            return $"\n--- STDOUT ---\n{stdout}\n--- STDERR ---\n{stderr}";
        }
        catch (Exception e)
        {
            return $"\n(could not fetch container logs: {e.Message})";
        }
    }

    private static void DeleteDirectoryQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
