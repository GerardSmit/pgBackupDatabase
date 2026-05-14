using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// VACUUM FULL on the source takes an ACCESS EXCLUSIVE lock on the
/// rewritten relation, rewrites its files, and changes its relfilenode.
/// A backup running concurrently must either block on the lock, finish
/// before the rewrite, or see a consistent snapshot — never a half-state
/// where the relfilenode and the row data disagree.
/// </summary>
public sealed class ConcurrentVacuumFullTests
{
    private readonly PgContainerFixture _pg;

    public ConcurrentVacuumFullTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task FullBackup_Survives_Concurrent_VacuumFull_On_Other_Table()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();

        // Two tables. We backup while VACUUM FULL rewrites the "noisy"
        // one. The backup must produce a valid file that restores cleanly
        // and reproduces both tables' contents exactly.
        await src.ExecAsync(
            "CREATE TABLE quiet(id int PRIMARY KEY, v text);" +
            "INSERT INTO quiet(id, v) " +
            "SELECT g, md5(g::text) FROM generate_series(1, 10000) g;" +
            "CREATE TABLE noisy(id int PRIMARY KEY, payload text);" +
            "INSERT INTO noisy(id, payload) " +
            "SELECT g, repeat('x', 200) FROM generate_series(1, 20000) g;" +
            "DELETE FROM noisy WHERE id % 2 = 0;",
            timeoutSeconds: 120);

        var quietRows = await ScalarLongAsync(src, "SELECT count(*) FROM quiet");
        var noisyRows = await ScalarLongAsync(src, "SELECT count(*) FROM noisy");
        var quietSum = await ScalarLongAsync(src,
            "SELECT coalesce(sum(id),0)::bigint FROM quiet");

        var full = Helpers.BackupPath("vacfull");

        // Drive concurrent VACUUM FULL on noisy in a SEPARATE connection
        // so it does not interleave with the backup call's session.
        var dbName = src.Database!;
        var vacuumTask = Task.Run(async () =>
        {
            await using var vc = await _pg.ConnectToAsync(dbName);
            // Loop a few VACUUM FULL passes during the backup window.
            for (var i = 0; i < 4; i++)
            {
                await using var cmd = vc.CreateCommand();
                cmd.CommandText = "VACUUM (FULL, ANALYZE) noisy";
                cmd.CommandTimeout = 120;
                try { await cmd.ExecuteNonQueryAsync(); } catch { /* may race */ }
            }
        });

        await src.BackupFullAsync(full, compress: true, commandTimeoutSeconds: 600);
        await vacuumTask;
        await src.CloseAsync();

        var target = "vacfull_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            var qc = await ScalarLongAsync(r, "SELECT count(*) FROM quiet");
            var qs = await ScalarLongAsync(r,
                "SELECT coalesce(sum(id),0)::bigint FROM quiet");
            Assert.Equal(quietRows, qc);
            Assert.Equal(quietSum, qs);

            // noisy table is present and self-consistent. Row count may
            // reflect the snapshot the backup saw; both states (pre- and
            // post-VACUUM FULL) have the same count because VACUUM FULL
            // does not change row visibility, only physical layout.
            var nc = await ScalarLongAsync(r, "SELECT count(*) FROM noisy");
            Assert.Equal(noisyRows, nc);

            // Payload integrity: every surviving row still has 200 x's.
            var corrupt = await ScalarLongAsync(r,
                "SELECT count(*) FROM noisy WHERE payload <> repeat('x', 200)");
            Assert.Equal(0L, corrupt);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    private static async Task<long> ScalarLongAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        return (long)(await cmd.ExecuteScalarAsync(
            TestContext.Current.CancellationToken))!;
    }

    private async Task RestoreAsync(string target, string file)
    {
        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
        cmd.Parameters.AddWithValue("files", new[] { file });
        cmd.Parameters.AddWithValue("tgt", target);
        cmd.CommandTimeout = 600;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        NpgsqlConnection.ClearAllPools();
    }
}
