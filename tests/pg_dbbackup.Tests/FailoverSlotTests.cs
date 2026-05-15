using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class FailoverSlotTests
{
    private const string PostgresUser = "postgres";
    private const string PostgresPassword = "postgres";
    private const string PostgresDb = "postgres";

    [Fact]
    public async Task FullLog_Refuses_To_Continue_After_Promotion_When_Failover_Slot_Not_Ready()
    {
        var image = await CachedPgDbBackupImage.BuildAsync(Helpers.PostgresImage, Helpers.PgMajor);
        var dbName = "failover_" + Guid.NewGuid().ToString("N")[..8];
        var targetDb = "failover_restored_" + Guid.NewGuid().ToString("N")[..8];
        const string fullPath = "/tmp/failover_full.bak";
        const string logPath = "/tmp/failover_log.bak";
        var hostFullPath = Path.Combine(
            Path.GetTempPath(),
            $"pgdbbackup_failover_{Guid.NewGuid():N}.bak");

        await using var network = new NetworkBuilder().Build();
        await network.CreateAsync(TestContext.Current.CancellationToken);

        IContainer? primary = null;
        IContainer? standby = null;

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
                await ExecSqlAsync(admin,
                    "ALTER SYSTEM SET synchronized_standby_slots = 'standby_slot'");
                await ExecSqlAsync(admin, "SELECT pg_reload_conf()");
            }

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

                await BackupAsync(source, "full", fullPath, null);

                var slotName = await ScalarAsync<string>(source,
                    "SELECT '_pg_dbbackup_' || " +
                    "(SELECT oid::text FROM pg_database WHERE datname = current_database())");

                await WaitForFailoverSlotObservedAsync(standby, dbName, slotName);

                await using (var standbyAdmin = await ConnectAsync(standby, PostgresDb))
                {
                    Assert.False(await ScalarAsync<bool>(standbyAdmin,
                        "SELECT dbbackup.pg_dbbackup_failover_slot_ready(@db)",
                        ("db", dbName)));
                    Assert.False(await ScalarAsync<bool>(standbyAdmin,
                        "SELECT dbbackup.pg_dbbackup_wait_failover_slot_ready(@db, 1)",
                        ("db", dbName)));
                    Assert.True(await ScalarAsync<bool>(standbyAdmin,
                        "SELECT synced AND temporary AND NOT standby_ready " +
                        "FROM dbbackup.pg_dbbackup_failover_slot_status(@db)",
                        ("db", dbName)));
                }

                await ExecSqlAsync(source,
                    "INSERT INTO items VALUES (2, 'after-failover')");
                var replayLsn = await ScalarAsync<string>(source,
                    "SELECT pg_current_wal_lsn()::text");

                await WaitForConditionAsync(async () =>
                {
                    await using var standbyAdmin = await ConnectAsync(standby, PostgresDb);
                    return await ScalarAsync<bool>(standbyAdmin,
                        "SELECT pg_last_wal_replay_lsn() >= @lsn::pg_lsn",
                        ("lsn", replayLsn));
                }, "standby replay catch-up");
            }

            await DockerCpFromAsync(primary, fullPath, hostFullPath);
            await DockerCpToAsync(hostFullPath, standby, fullPath);
            await ShellOrThrowAsync(standby,
                $"chown postgres:postgres {fullPath} && chmod 644 {fullPath}",
                "make copied backup readable by postgres");

            await primary.StopAsync(TestContext.Current.CancellationToken);
            await using (var standbyAdmin = await ConnectAsync(standby, PostgresDb))
            {
                Assert.True(await ScalarAsync<bool>(standbyAdmin, "SELECT pg_promote(true, 60)"));
            }

            await WaitForConditionAsync(async () =>
            {
                await using var standbyAdmin = await ConnectAsync(standby, PostgresDb);
                return await ScalarAsync<bool>(standbyAdmin,
                    "SELECT NOT pg_is_in_recovery()");
            }, "standby promotion");

            await using (var promoted = await ConnectAsync(standby, dbName))
            {
                var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
                    await BackupAsync(promoted, "log", logPath, fullPath));
                Assert.Contains("does not exist", ex.MessageText);
            }
        }
        finally
        {
            try { File.Delete(hostFullPath); } catch { }

            if (standby is not null)
            {
                try
                {
                    await using var admin = await ConnectAsync(standby, PostgresDb);
                    await ExecSqlAsync(admin,
                        $"DROP DATABASE IF EXISTS \"{targetDb}\" WITH (FORCE);" +
                        $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE);");
                }
                catch { }

                await standby.DisposeAsync();
            }

            if (primary is not null)
                await primary.DisposeAsync();
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
            "mkdir -p \"$PGDATA\"; " +
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

    private static string ConnectionString(IContainer container, string database = PostgresDb) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Port = container.GetMappedPublicPort(5432),
            Username = PostgresUser,
            Password = PostgresPassword,
            Database = database,
            Pooling = false,
        }.ConnectionString;

    private static async Task<NpgsqlConnection> ConnectAsync(
        IContainer container,
        string database)
    {
        var conn = new NpgsqlConnection(ConnectionString(container, database));
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        return conn;
    }

    private static async Task WaitForReadyConnectionAsync(
        IContainer container,
        string label)
    {
        var connectionString = ConnectionString(container);
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

    private static async Task WaitForFailoverSlotObservedAsync(
        IContainer standby,
        string dbName,
        string slotName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        var lastState = "<not checked>";

        while (DateTime.UtcNow < deadline)
        {
            await using var standbyAdmin = await ConnectAsync(standby, PostgresDb);
            lastState = await ScalarAsync<string>(standbyAdmin,
                "SELECT COALESCE((" +
                "  SELECT to_jsonb(s)::text FROM pg_replication_slots s " +
                "  WHERE slot_name = @slot), '<missing>')",
                ("slot", slotName));

            var observed = await ScalarAsync<bool>(standbyAdmin,
                "SELECT COALESCE((" +
                "  SELECT slot_exists " +
                "  FROM dbbackup.pg_dbbackup_failover_slot_status(@db)), false)",
                ("db", dbName));
            if (observed)
                return;

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException(
            "Timed out waiting for synced logical failover slot to appear " +
            $"on standby. Last slot state: {lastState}{await LogsAsync(standby)}");
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

    private static async Task DockerCpFromAsync(
        IContainer container,
        string containerPath,
        string hostPath)
    {
        await DockerCpAsync($"{container.Id}:{containerPath}", hostPath);
    }

    private static async Task DockerCpToAsync(
        string hostPath,
        IContainer container,
        string containerPath)
    {
        await DockerCpAsync(hostPath, $"{container.Id}:{containerPath}");
    }

    private static async Task DockerCpAsync(string source, string destination)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("cp");
        psi.ArgumentList.Add(source);
        psi.ArgumentList.Add(destination);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start docker");
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"docker cp {source} {destination} failed: {await stderr}");
    }
}
