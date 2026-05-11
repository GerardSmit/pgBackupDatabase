using System.Diagnostics;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace PgDbBackup.Tests.Fixtures;

internal sealed class ImmediateReadyWait : IWaitUntil
{
    public Task<bool> UntilAsync(IContainer container) => Task.FromResult(true);
}

public sealed class PgContainerFixture : IAsyncLifetime
{
    private const string PostgresImage = "postgres:17";
    private const string PostgresUser = "postgres";
    private const string PostgresPassword = "postgres";
    private const string PostgresDb = "postgres";

    private PostgreSqlContainer _container = null!;

    public string ConnectionString => _container.GetConnectionString();

    public static string ProjectRoot { get; } = LocateProjectRoot();

    public async ValueTask InitializeAsync()
    {
        _container = new PostgreSqlBuilder()
            .WithImage(PostgresImage)
            .WithUsername(PostgresUser)
            .WithPassword(PostgresPassword)
            .WithDatabase(PostgresDb)
            .WithCommand(
                "-c", "wal_level=replica",
                "-c", "track_commit_timestamp=on",
                "-c", "max_wal_senders=5",
                "-c", "wal_keep_size=64",
                "-c", "summarize_wal=on")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .AddCustomWaitStrategy(new ImmediateReadyWait()))
            .Build();

        await _container.StartAsync();
        await WaitForReadyConnectionAsync();
        await InstallBuildDepsAsync();
        await CopySourceAsync();
        await BuildAndInstallAsync();
        await CopyExtensionMetadataAsync();
        await EnsureExtensionInPostgresDbAsync();
    }

    private async Task EnsureExtensionInPostgresDbAsync()
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS pg_dbbackup";
        await cmd.ExecuteNonQueryAsync();
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
        await using var admin = await AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)";
        cmd.CommandTimeout = 60;
        await cmd.ExecuteNonQueryAsync();
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

    public async Task<NpgsqlConnection> CreateFreshDbWithExtensionAsync()
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
        return conn;
    }

    private async Task InstallBuildDepsAsync()
    {
        var result = await ShellAsync(
            "apt-get update -qq && apt-get install -y -qq postgresql-server-dev-17 build-essential libzstd-dev libssl-dev");
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Installing build deps failed (exit {result.ExitCode}):\nstdout:\n{result.Stdout}\nstderr:\n{result.Stderr}");
        }
    }

    private async Task CopySourceAsync()
    {
        await ShellOrThrowAsync("mkdir -p /build/src /build/sql", "create /build dirs");

        var srcDir = Path.Combine(ProjectRoot, "src");
        foreach (var file in Directory.EnumerateFiles(srcDir))
        {
            await DockerCpAsync(file, $"/build/src/{Path.GetFileName(file)}");
        }

        await DockerCpAsync(Path.Combine(ProjectRoot, "Makefile"), "/build/Makefile");
        await DockerCpAsync(Path.Combine(ProjectRoot, "pg_dbbackup.control"), "/build/pg_dbbackup.control");

        var sqlDir = Path.Combine(ProjectRoot, "sql");
        foreach (var file in Directory.EnumerateFiles(sqlDir, "*.sql"))
        {
            await DockerCpAsync(file, $"/build/sql/{Path.GetFileName(file)}");
        }
    }

    private async Task DockerCpAsync(string hostPath, string containerPath)
    {
        var psi = new ProcessStartInfo("docker", $"cp \"{hostPath}\" {_container.Id}:{containerPath}")
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
            Path.Combine(ProjectRoot, "pg_dbbackup.control"),
            $"{extDir}/pg_dbbackup.control");

        var sqlDir = Path.Combine(ProjectRoot, "sql");
        foreach (var file in Directory.EnumerateFiles(sqlDir, "*.sql"))
        {
            await DockerCpAsync(file, $"{extDir}/{Path.GetFileName(file)}");
        }
    }

    private async Task WaitForReadyConnectionAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
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

        string logs = "";
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
            $"PostgreSQL not accepting connections on {ConnectionString}{logs}",
            last);
    }

    public async Task<ExecResult> ShellAsync(string command)
    {
        var raw = await _container.ExecAsync(new[] { "sh", "-c", command });
        return new ExecResult(raw.ExitCode, raw.Stdout ?? string.Empty, raw.Stderr ?? string.Empty);
    }

    public async Task<byte[]> ReadContainerFileAsync(string containerPath)
    {
        var result = await ShellAsync($"base64 -w 0 {containerPath}");
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"base64 of {containerPath} failed (exit {result.ExitCode}): {result.Stderr}");
        return Convert.FromBase64String(result.Stdout.Trim());
    }

    private async Task<ExecResult> ShellOrThrowAsync(string command, string label)
    {
        var result = await ShellAsync(command);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{label} failed (exit {result.ExitCode}):\nstdout:\n{result.Stdout}\nstderr:\n{result.Stderr}");
        }
        return result;
    }

    private static string LocateProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "pg_dbbackup.control")) &&
                File.Exists(Path.Combine(dir.FullName, "Makefile")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate project root (pg_dbbackup.control + Makefile) walking up from " + AppContext.BaseDirectory);
    }

    public readonly record struct ExecResult(long ExitCode, string Stdout, string Stderr);
}
