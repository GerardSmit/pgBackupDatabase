using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Testcontainers.PostgreSql;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Cross PostgreSQL major version restore. Takes FULL + LOG on the primary
/// fixture's PG version and restores into a separately spawned container
/// running the other supported major. Verifies row count, schema, and
/// chain replay survive across versions.
/// </summary>
public sealed class CrossPgVersionRestoreTests
{
    private readonly PgContainerFixture _pg;

    public CrossPgVersionRestoreTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Backup_On_Major_X_Restores_On_Other_Major()
    {
        var srcMajor = Helpers.PgMajor;
        var targetMajor = srcMajor == "17" ? "18" : "17";

        string? sourceDb = null;
        var fullPath = Helpers.BackupPath("cross_ver_full");
        var logPath = Helpers.BackupPath("cross_ver_log");

        try
        {
            await using (var src = await _pg.CreateFreshDbWithExtensionAsync())
            {
                sourceDb = src.Database!;
                await src.SetModeFullAsync();
                await src.ExecAsync(
                    "CREATE TABLE inventory(" +
                    "  id int PRIMARY KEY," +
                    "  sku text NOT NULL," +
                    "  qty int NOT NULL," +
                    "  added timestamptz NOT NULL DEFAULT now()" +
                    ");" +
                    "CREATE INDEX inventory_sku_idx ON inventory(sku);" +
                    "INSERT INTO inventory(id, sku, qty) " +
                    "SELECT g, 'sku-'||g, g*2 " +
                    "FROM generate_series(1, 1000) g;");

                await src.BackupFullAsync(fullPath, compress: true,
                    commandTimeoutSeconds: 180);

                await src.ExecAsync(
                    "INSERT INTO inventory(id, sku, qty) " +
                    "VALUES (1001, 'sku-late', 7);" +
                    "UPDATE inventory SET qty = qty + 1 WHERE id = 1;");

                await src.BackupLogAsync(logPath, fullPath, compress: true,
                    commandTimeoutSeconds: 180);
            }

            var fullBytes = await _pg.ReadContainerFileAsync(fullPath);
            var logBytes = await _pg.ReadContainerFileAsync(logPath);

            await using var targetPg = await VersionedPgContainer.StartAsync(
                $"postgres:{targetMajor}", targetMajor);

            var tFull = Helpers.BackupPath("cross_ver_target_full");
            var tLog = Helpers.BackupPath("cross_ver_target_log");
            await targetPg.WriteContainerFileAsync(tFull, fullBytes);
            await targetPg.WriteContainerFileAsync(tLog, logBytes);

            var targetDb = "cross_ver_r_" + Guid.NewGuid().ToString("N")[..8];
            await using (var admin = await targetPg.AdminAsync())
            await using (var cmd = admin.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
                cmd.Parameters.AddWithValue("files", new[] { tFull, tLog });
                cmd.Parameters.AddWithValue("tgt", targetDb);
                cmd.CommandTimeout = 300;
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            NpgsqlConnection.ClearAllPools();

            await using var restored = await targetPg.ConnectToAsync(targetDb);
            await using (var cmd = restored.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM inventory";
                Assert.Equal(1001L, (long)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
            await using (var cmd = restored.CreateCommand())
            {
                cmd.CommandText = "SELECT qty FROM inventory WHERE id = 1";
                Assert.Equal(3, (int)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
            await using (var cmd = restored.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM pg_indexes " +
                    "WHERE tablename = 'inventory' AND indexname = 'inventory_sku_idx'";
                Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            if (sourceDb is not null)
                try { await _pg.DropDbAsync(sourceDb); } catch { }
        }
    }

    private sealed class VersionedPgContainer : IAsyncDisposable
    {
        private readonly PostgreSqlContainer _container;

        private VersionedPgContainer(PostgreSqlContainer container) =>
            _container = container;

        private string ConnectionString => _container.GetConnectionString();

        public static async Task<VersionedPgContainer> StartAsync(
            string image, string pgMajor)
        {
            var built = await CachedPgDbBackupImage.BuildAsync(image, pgMajor);
            var container = new PostgreSqlBuilder(built)
                .WithUsername("postgres")
                .WithPassword("postgres")
                .WithDatabase("postgres")
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

            var w = new VersionedPgContainer(container);
            await container.StartAsync(TestContext.Current.CancellationToken);
            await w.WaitReady();
            await w.EnsureExtensionInPostgres();
            return w;
        }

        public async ValueTask DisposeAsync() => await _container.DisposeAsync();

        public async Task<NpgsqlConnection> AdminAsync()
        {
            var c = new NpgsqlConnection(ConnectionString);
            await c.OpenAsync(TestContext.Current.CancellationToken);
            return c;
        }

        public async Task<NpgsqlConnection> ConnectToAsync(string dbName)
        {
            var b = new NpgsqlConnectionStringBuilder(ConnectionString)
            {
                Database = dbName
            };
            var c = new NpgsqlConnection(b.ConnectionString);
            await c.OpenAsync(TestContext.Current.CancellationToken);
            return c;
        }

        public async Task WriteContainerFileAsync(string containerPath, byte[] bytes)
        {
            var hostPath = Path.Combine(Path.GetTempPath(),
                $"pgdbbackup_{Guid.NewGuid():N}.bak");
            await File.WriteAllBytesAsync(hostPath, bytes);
            try
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
                using var p = Process.Start(psi)
                    ?? throw new InvalidOperationException("docker missing");
                var stderr = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                if (p.ExitCode != 0)
                    throw new InvalidOperationException(
                        $"docker cp failed: {await stderr}");
            }
            finally
            {
                try { File.Delete(hostPath); } catch { }
            }
        }

        private async Task EnsureExtensionInPostgres()
        {
            await using var c = await AdminAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS pg_dbbackup";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        private async Task WaitReady()
        {
            var deadline = DateTime.UtcNow.AddSeconds(120);
            Exception? last = null;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    await using var c = await AdminAsync();
                    await using var cmd = c.CreateCommand();
                    cmd.CommandText = "SELECT 1";
                    await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
                    return;
                }
                catch (Exception e)
                {
                    last = e;
                    await Task.Delay(1000, TestContext.Current.CancellationToken);
                }
            }
            throw new InvalidOperationException("postgres not ready", last);
        }
    }
}
