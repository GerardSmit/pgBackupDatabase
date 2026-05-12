using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Testcontainers.PostgreSql;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class CrossContainerRestoreTests
{
    private readonly PgContainerFixture _pg;

    public CrossContainerRestoreTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task FullRestore_BackupChain_Restores_In_Fresh_Postgres_Container()
    {
        var roleName = "cross_role_" + Guid.NewGuid().ToString("N")[..8];
        string? sourceDb = null;

        await using (var admin = await _pg.AdminAsync())
        await using (var cmd = admin.CreateCommand())
        {
            cmd.CommandText = $"CREATE ROLE \"{roleName}\"";
            await cmd.ExecuteNonQueryAsync();
        }

        var fullPath = Helpers.BackupPath("cross_container_full");
        var logPath = Helpers.BackupPath("cross_container_log");

        try
        {
            await using (var src = await _pg.CreateFreshDbWithExtensionAsync())
            {
                sourceDb = src.Database!;
                await src.SetModeFullAsync();
                await src.ExecAsync(
                    "CREATE EXTENSION pgcrypto;" +
                    "CREATE SCHEMA app;" +
                    "CREATE TABLE app.items(" +
                    "id int PRIMARY KEY, " +
                    "code text NOT NULL, " +
                    "token uuid NOT NULL DEFAULT gen_random_uuid());" +
                    "CREATE UNIQUE INDEX items_code_idx ON app.items(code);" +
                    $"ALTER TABLE app.items OWNER TO \"{roleName}\";" +
                    $"GRANT SELECT ON app.items TO \"{roleName}\";" +
                    "INSERT INTO app.items(id, code) VALUES (1, 'full');");

                await src.BackupFullAsync(fullPath, compress: true);

                await src.ExecAsync("INSERT INTO app.items(id, code) VALUES (2, 'log');");
                await src.BackupLogAsync(logPath, fullPath, compress: true);
            }

            var fullBytes = await _pg.ReadContainerFileAsync(fullPath);
            var logBytes = await _pg.ReadContainerFileAsync(logPath);

            await using var targetPg = await SecondaryPgContainer.StartAsync();
            var targetFullPath = Helpers.BackupPath("fresh_container_full");
            var targetLogPath = Helpers.BackupPath("fresh_container_log");
            await targetPg.WriteContainerFileAsync(targetFullPath, fullBytes);
            await targetPg.WriteContainerFileAsync(targetLogPath, logBytes);

            var targetDb = "fresh_restore_" + Guid.NewGuid().ToString("N")[..8];
            await RestoreAsync(targetPg, targetDb, targetFullPath, targetLogPath);

            await using var restored = await targetPg.ConnectToAsync(targetDb);
            await using (var cmd = restored.CreateCommand())
            {
                cmd.CommandText = "SELECT array_agg(code ORDER BY id) FROM app.items";
                Assert.Equal(new[] { "full", "log" }, (string[])(await cmd.ExecuteScalarAsync())!);
            }

            await using (var cmd = restored.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM pg_indexes " +
                    "WHERE schemaname = 'app' AND indexname = 'items_code_idx'";
                Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
            }

            await using (var cmd = restored.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT pg_get_userbyid(c.relowner) " +
                    "FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
                    "WHERE n.nspname = 'app' AND c.relname = 'items'";
                Assert.Equal(roleName, (string)(await cmd.ExecuteScalarAsync())!);
            }

            await using (var cmd = restored.CreateCommand())
            {
                cmd.CommandText = "SELECT has_table_privilege(@role, 'app.items', 'SELECT')";
                cmd.Parameters.AddWithValue("role", roleName);
                Assert.True((bool)(await cmd.ExecuteScalarAsync())!);
            }

            await using (var cmd = restored.CreateCommand())
            {
                cmd.CommandText = "SELECT gen_random_uuid() IS NOT NULL";
                Assert.True((bool)(await cmd.ExecuteScalarAsync())!);
            }
        }
        finally
        {
            if (sourceDb is not null)
            {
                try { await _pg.DropDbAsync(sourceDb); } catch { }
            }

            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"DROP ROLE IF EXISTS \"{roleName}\"";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task RestoreAsync(
        SecondaryPgContainer targetPg,
        string targetDb,
        params string[] files)
    {
        await using var admin = await targetPg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbrestore(@db, @files::text[], target_db := @target)";
        cmd.Parameters.AddWithValue("db", "ignored");
        cmd.Parameters.AddWithValue("files", files);
        cmd.Parameters.AddWithValue("target", targetDb);
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync();
        NpgsqlConnection.ClearAllPools();
    }

    private sealed class SecondaryPgContainer : IAsyncDisposable
    {
        private const string PostgresImage = "postgres:17";
        private const string PostgresUser = "postgres";
        private const string PostgresPassword = "postgres";
        private const string PostgresDb = "postgres";

        private readonly PostgreSqlContainer _container;

        private SecondaryPgContainer(PostgreSqlContainer container)
        {
            _container = container;
        }

        private string ConnectionString => _container.GetConnectionString();

        public static async Task<SecondaryPgContainer> StartAsync()
        {
            var image = await CachedPgDbBackupImage.BuildAsync(PostgresImage, "17");
            var container = new PostgreSqlBuilder()
                .WithImage(image)
                .WithUsername(PostgresUser)
                .WithPassword(PostgresPassword)
                .WithDatabase(PostgresDb)
                .WithCommand(
                    "-c", "shared_preload_libraries=pg_dbbackup",
                    "-c", "wal_level=logical",
                    "-c", "max_replication_slots=100",
                    "-c", "track_commit_timestamp=on",
                    "-c", "max_wal_senders=5",
                    "-c", "wal_keep_size=64",
                    "-c", "summarize_wal=on")
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .AddCustomWaitStrategy(new ImmediateReadyWait()))
                .Build();

            var wrapper = new SecondaryPgContainer(container);
            await container.StartAsync();
            await wrapper.WaitForReadyConnectionAsync();
            await wrapper.EnsureExtensionInPostgresDbAsync();
            return wrapper;
        }

        public async ValueTask DisposeAsync()
        {
            await _container.DisposeAsync();
        }

        public async Task<NpgsqlConnection> AdminAsync()
        {
            var conn = new NpgsqlConnection(ConnectionString);
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

        public async Task WriteContainerFileAsync(string containerPath, byte[] bytes)
        {
            var hostPath = Path.Combine(
                Path.GetTempPath(),
                $"pgdbbackup_{Guid.NewGuid():N}.bak");
            await File.WriteAllBytesAsync(hostPath, bytes);
            try
            {
                await DockerCpAsync(hostPath, containerPath);
            }
            finally
            {
                try { File.Delete(hostPath); } catch { }
            }
        }

        private async Task EnsureExtensionInPostgresDbAsync()
        {
            await using var conn = await AdminAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS pg_dbbackup";
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task WaitForReadyConnectionAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(90);
            Exception? last = null;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    await using var conn = await AdminAsync();
                    await using var cmd = conn.CreateCommand();
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

            var logs = "";
            try
            {
                var (stdout, stderr) = await _container.GetLogsAsync();
                logs = $"\n--- STDOUT ---\n{stdout}\n--- STDERR ---\n{stderr}";
            }
            catch (Exception e)
            {
                logs = $"\n(could not fetch logs: {e.Message})";
            }

            throw new InvalidOperationException(
                $"PostgreSQL not accepting connections on {ConnectionString}{logs}",
                last);
        }

        private async Task DockerCpAsync(string hostPath, string containerPath)
        {
            var psi = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("cp");
            psi.ArgumentList.Add(hostPath);
            psi.ArgumentList.Add($"{_container.Id}:{containerPath}");

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("failed to start docker");
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"docker cp {hostPath} -> {containerPath} failed: {await stderr}");
            }
        }
    }
}
