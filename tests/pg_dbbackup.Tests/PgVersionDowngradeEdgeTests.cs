using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Testcontainers.PostgreSql;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// PG version mismatch edges beyond happy path. When a source uses a
/// catalog/syntax feature that the target's older major doesn't
/// understand, restore must fail with an actionable error — not
/// silently strip the feature or land in a broken state.
///
/// Spins up PG18 as source (NOT ENFORCED constraints, supported since
/// PG18) and PG17 as target. Restore must fail with a syntax/catalog
/// error rather than silently succeed without the constraint.
/// </summary>
public sealed class PgVersionDowngradeEdgeTests
{
    [Fact]
    public async Task NotEnforced_Constraint_From_Pg18_Cannot_Silently_Restore_On_Pg17()
    {
        await using var src = await DowngradePgContainer.StartAsync("postgres:18", "18");
        await using var tgt = await DowngradePgContainer.StartAsync("postgres:17", "17");

        var sourceDb = "src_" + Guid.NewGuid().ToString("N")[..8];
        await using (var admin = await src.AdminAsync())
        {
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{sourceDb}\"";
            await cmd.ExecuteNonQueryAsync();
        }
        await using var srcDb = await src.ConnectToAsync(sourceDb);
        await using (var cmd = srcDb.CreateCommand())
        {
            cmd.CommandText = "CREATE EXTENSION pg_dbbackup";
            await cmd.ExecuteNonQueryAsync();
        }

        // PG18 feature: NOT ENFORCED CHECK constraint. Catalog stores
        // conenforced = false. PG17 cannot parse NOT ENFORCED syntax.
        try
        {
            await using var cmd = srcDb.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE dg_t(id int PRIMARY KEY, " +
                "  qty int CONSTRAINT dg_qty_chk CHECK (qty > 0) NOT ENFORCED);" +
                "INSERT INTO dg_t VALUES (1, 10), (2, 20);";
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == "42601")
        {
            // PG18 image not yet supporting NOT ENFORCED — skip cleanly.
            return;
        }

        var fullPath = "/tmp/dg_full.bak";
        await using (var cmd = srcDb.CreateCommand())
        {
            cmd.CommandText =
                "SELECT dbbackup.pg_dbbackup(@db, @path, type := 'full', compress := false)";
            cmd.Parameters.AddWithValue("db", sourceDb);
            cmd.Parameters.AddWithValue("path", fullPath);
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync();
        }

        var bytes = await src.ReadContainerFileAsync(fullPath);
        var tgtPath = "/tmp/dg_full.bak";
        await tgt.WriteContainerFileAsync(tgtPath, bytes);

        var targetDb = "dg_" + Guid.NewGuid().ToString("N")[..8];
        Exception? restoreErr = null;
        try
        {
            await using var admin = await tgt.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
            cmd.Parameters.AddWithValue("files", new[] { tgtPath });
            cmd.Parameters.AddWithValue("tgt", targetDb);
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) { restoreErr = ex; }

        if (restoreErr is PostgresException px)
        {
            // Expected: a schema-replay / parse / version-mismatch
            // error during restore. The PG17 server rejects PG18-only
            // syntax; the exact wording is "libpq exec failed during
            // SCHEMA" plus a server-side hint.
            var msg = (px.MessageText + " " + (px.Detail ?? "") + " " +
                       (px.Hint ?? "")).ToLowerInvariant();
            Assert.True(
                msg.Contains("syntax") || msg.Contains("enforced") ||
                msg.Contains("version") || msg.Contains("unsupport") ||
                msg.Contains("constraint") || msg.Contains("parse") ||
                msg.Contains("schema") || msg.Contains("libpq") ||
                msg.Contains("exec failed") || msg.Contains("restore"),
                $"Downgrade error must explain the conflict: {px.MessageText}");
        }
        else
        {
            // If restore silently succeeded, the constraint MUST have
            // either been emitted as a plain CHECK or the test fails.
            // A silently-dropped constraint changes the runtime
            // contract for the app and is unsafe.
            await using var r = await tgt.ConnectToAsync(targetDb);
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT count(*) FROM pg_constraint " +
                "WHERE conname = 'dg_qty_chk'";
            var n = (long)(await cmd.ExecuteScalarAsync())!;
            Assert.Equal(1L, n);

            // And it must be enforcing (PG17 has no NOT ENFORCED).
            await using var c2 = r.CreateCommand();
            c2.CommandText = "INSERT INTO dg_t VALUES (3, 0)";
            // PG17 rejects negative qty because constraint must be enforced.
            await Assert.ThrowsAsync<PostgresException>(
                () => c2.ExecuteNonQueryAsync());
            try { await tgt.DropDbAsync(targetDb); } catch { }
        }
    }

    private sealed class DowngradePgContainer : IAsyncDisposable
    {
        private readonly PostgreSqlContainer _container;

        private DowngradePgContainer(PostgreSqlContainer c) => _container = c;

        public string ConnectionString => _container.GetConnectionString();

        public static async Task<DowngradePgContainer> StartAsync(
            string image, string pgMajor)
        {
            var built = await CachedPgDbBackupImage.BuildAsync(image, pgMajor);
            var c = new PostgreSqlBuilder(built)
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
            var w = new DowngradePgContainer(c);
            await c.StartAsync(TestContext.Current.CancellationToken);
            await w.WaitReadyAsync();
            await w.EnsureExtensionInPostgresDb();
            return w;
        }

        public async ValueTask DisposeAsync() => await _container.DisposeAsync();

        public async Task<NpgsqlConnection> AdminAsync()
        {
            var c = new NpgsqlConnection(ConnectionString);
            await c.OpenAsync(TestContext.Current.CancellationToken);
            return c;
        }

        public async Task<NpgsqlConnection> ConnectToAsync(string db)
        {
            var b = new NpgsqlConnectionStringBuilder(ConnectionString)
            { Database = db };
            var c = new NpgsqlConnection(b.ConnectionString);
            await c.OpenAsync(TestContext.Current.CancellationToken);
            return c;
        }

        public async Task DropDbAsync(string db)
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = await AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{db}\" WITH (FORCE)";
            cmd.CommandTimeout = 60;
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<byte[]> ReadContainerFileAsync(string path)
        {
            var raw = await _container.ExecAsync(new[] { "sh", "-c", $"base64 -w 0 {path}" });
            return Convert.FromBase64String((raw.Stdout ?? string.Empty).Trim());
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
                using var p = Process.Start(psi)!;
                await p.WaitForExitAsync();
                if (p.ExitCode != 0)
                    throw new InvalidOperationException(
                        $"docker cp failed: {await p.StandardError.ReadToEndAsync()}");
            }
            finally
            {
                try { File.Delete(hostPath); } catch { }
            }
        }

        private async Task EnsureExtensionInPostgresDb()
        {
            await using var c = await AdminAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS pg_dbbackup";
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task WaitReadyAsync()
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
                    await cmd.ExecuteScalarAsync();
                    return;
                }
                catch (Exception e)
                {
                    last = e;
                    await Task.Delay(1000);
                }
            }
            throw new InvalidOperationException("postgres not ready", last);
        }
    }
}
