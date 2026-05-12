using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace PgDbBackup.Tests.Fixtures;

public sealed class PgWithExtensionsFixture : IAsyncLifetime
{
    private const string Image = "gerardsmit/pg_textsearch:latest";
    private const string User = "postgres";
    private const string Password = "postgres";
    private const string Db = "postgres";

    private PostgreSqlContainer _container = null!;

    public string ConnectionString => _container.GetConnectionString();

    public bool HasTimescaleDb { get; private set; }
    public bool HasPgvector { get; private set; }
    public bool HasPgTextsearch { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var pgMajor = await CachedPgDbBackupImage.DetectPgMajorAsync(Image);
        var image = await CachedPgDbBackupImage.BuildAsync(Image, pgMajor);

        _container = new PostgreSqlBuilder()
            .WithImage(image)
            .WithUsername(User)
            .WithPassword(Password)
            .WithDatabase(Db)
            .WithCommand(
                "-c", "shared_preload_libraries=timescaledb,pg_textsearch,pg_dbbackup",
                "-c", "wal_level=logical",
                "-c", "max_replication_slots=100",
                "-c", "track_commit_timestamp=on",
                "-c", "max_wal_senders=5",
                "-c", "wal_keep_size=64",
                "-c", "summarize_wal=on",
                "-c", "timescaledb.max_background_workers=0",
                "-c", "timescaledb.telemetry_level=off")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .AddCustomWaitStrategy(new ImmediateReadyWait()))
            .Build();

        await _container.StartAsync();
        await WaitForReadyConnectionAsync();
        await DetectExtensionsAsync();
        await EnsureExtensionInPostgresDbAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public NpgsqlConnection CreateConnection() => new(ConnectionString);

    public async Task<NpgsqlConnection> AdminAsync()
    {
        var conn = CreateConnection();
        await conn.OpenAsync();
        return conn;
    }

    public async Task<bool> DbExistsAsync(string name)
    {
        await using var admin = await AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM pg_database WHERE datname = @n";
        cmd.Parameters.AddWithValue("n", name);
        var v = await cmd.ExecuteScalarAsync();
        return v != null;
    }

    public async Task<NpgsqlConnection> CreateFreshDbWithExtensionAsync(
        params string[] extraExtensions)
    {
        var dbName = "test_" + Guid.NewGuid().ToString("N")[..8];

        await using (var admin = CreateConnection())
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
            await cmd.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = dbName };
        var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE EXTENSION pg_dbbackup";
            await cmd.ExecuteNonQueryAsync();
        }
        foreach (var ext in extraExtensions)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE EXTENSION IF NOT EXISTS \"{ext}\" CASCADE";
            cmd.CommandTimeout = 60;
            await cmd.ExecuteNonQueryAsync();
        }
        return conn;
    }

    public async Task<NpgsqlConnection> ConnectToAsync(string dbName)
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = dbName,
        };
        var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    public async Task DropDbAsync(string name)
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = CreateConnection();
        await admin.OpenAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)";
        cmd.CommandTimeout = 60;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task DetectExtensionsAsync()
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM pg_available_extensions " +
            "WHERE name IN ('timescaledb','vector','pg_textsearch')";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(0);
            if (name == "timescaledb") HasTimescaleDb = true;
            else if (name == "vector") HasPgvector = true;
            else if (name == "pg_textsearch") HasPgTextsearch = true;
        }
    }

    private async Task EnsureExtensionInPostgresDbAsync()
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS pg_dbbackup";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InstallBuildDepsAsync()
    {
        var probe = await ShellAsync("pg_config --version");
        if (probe.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "pg_config missing in container; cannot build pg_dbbackup");
        }

        var pgMajor = await ShellOrThrowAsync(
            "pg_config --version | awk '{print $2}' | cut -d. -f1",
            "detect PG major");
        var major = pgMajor.Stdout.Trim();

        var result = await ShellAsync(
            $"apt-get update -qq && apt-get install -y -qq " +
            $"postgresql-server-dev-{major} build-essential libzstd-dev libssl-dev");
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Installing build deps failed (exit {result.ExitCode}):\n" +
                $"stdout:\n{result.Stdout}\nstderr:\n{result.Stderr}");
        }
    }

    private async Task CopySourceAsync()
    {
        await ShellOrThrowAsync("mkdir -p /build/src /build/sql", "create /build dirs");

        var root = PgContainerFixture.ProjectRoot;
        var srcDir = Path.Combine(root, "src");
        foreach (var file in Directory.EnumerateFiles(srcDir))
        {
            await DockerCpAsync(file, $"/build/src/{Path.GetFileName(file)}");
        }

        await DockerCpAsync(Path.Combine(root, "Makefile"), "/build/Makefile");
        await DockerCpAsync(Path.Combine(root, "pg_dbbackup.control"),
            "/build/pg_dbbackup.control");

        var sqlDir = Path.Combine(root, "sql");
        foreach (var file in Directory.EnumerateFiles(sqlDir, "*.sql"))
        {
            await DockerCpAsync(file, $"/build/sql/{Path.GetFileName(file)}");
        }
    }

    private async Task DockerCpAsync(string hostPath, string containerPath)
    {
        var psi = new ProcessStartInfo("docker",
            $"cp \"{hostPath}\" {_container.Id}:{containerPath}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            var err = await p.StandardError.ReadToEndAsync();
            throw new InvalidOperationException(
                $"docker cp {hostPath} -> {containerPath} failed: {err}");
        }
    }

    private async Task BuildAndInstallAsync()
    {
        var build = await ShellAsync("cd /build && make -j\"$(nproc)\"");
        if (build.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"make failed (exit {build.ExitCode}):\nstdout:\n{build.Stdout}\nstderr:\n{build.Stderr}");
        }

        var install = await ShellAsync("cd /build && make install");
        if (install.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"make install failed (exit {install.ExitCode}):\nstdout:\n{install.Stdout}\nstderr:\n{install.Stderr}");
        }
    }

    private async Task CopyExtensionMetadataAsync()
    {
        var shareDir = (await ShellOrThrowAsync("pg_config --sharedir", "pg_config --sharedir"))
            .Stdout.Trim();
        var extDir = $"{shareDir}/extension";
        await ShellOrThrowAsync($"mkdir -p {extDir}", "mkdir extension dir");

        await DockerCpAsync(
            Path.Combine(PgContainerFixture.ProjectRoot, "pg_dbbackup.control"),
            $"{extDir}/pg_dbbackup.control");

        var sqlDir = Path.Combine(PgContainerFixture.ProjectRoot, "sql");
        foreach (var file in Directory.EnumerateFiles(sqlDir, "*.sql"))
        {
            await DockerCpAsync(file, $"{extDir}/{Path.GetFileName(file)}");
        }
    }

    private async Task WaitForReadyConnectionAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var c = CreateConnection();
                await c.OpenAsync();
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT 1";
                await cmd.ExecuteScalarAsync();
                return;
            }
            catch (Exception e)
            {
                last = e;
                await Task.Delay(1000);
            }
        }

        string logs;
        try
        {
            var (stdout, stderr) = await _container.GetLogsAsync();
            logs = $"\n--- STDOUT ---\n{stdout}\n--- STDERR ---\n{stderr}";
        }
        catch (Exception le)
        {
            logs = $"\n(could not fetch logs: {le.Message})";
        }

        throw new InvalidOperationException(
            $"PostgreSQL (extensions image) not accepting connections on {ConnectionString}{logs}",
            last);
    }

    public async Task<PgContainerFixture.ExecResult> ShellAsync(string command)
    {
        var raw = await _container.ExecAsync(new[] { "sh", "-c", command });
        return new PgContainerFixture.ExecResult(
            raw.ExitCode, raw.Stdout ?? string.Empty, raw.Stderr ?? string.Empty);
    }

    public async Task<string> LogsAsync()
    {
        var (stdout, stderr) = await _container.GetLogsAsync();
        return $"--- STDOUT ---\n{stdout}\n--- STDERR ---\n{stderr}";
    }

    private async Task<PgContainerFixture.ExecResult> ShellOrThrowAsync(
        string command, string label)
    {
        var result = await ShellAsync(command);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{label} failed (exit {result.ExitCode}):\nstdout:\n{result.Stdout}\nstderr:\n{result.Stderr}");
        }
        return result;
    }
}
