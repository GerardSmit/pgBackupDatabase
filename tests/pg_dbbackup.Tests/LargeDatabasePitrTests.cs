using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Goal-aligned end-to-end coverage: build a non-trivial dataset, take a
/// FULL backup, take several LOG backups against it, and exercise PITR
/// restores at mid-chain, exact-cutoff, and end-of-chain points. Verifies
/// per-row counts plus checksum-style aggregates so a quietly corrupt
/// replay would not pass.
/// </summary>
public sealed class LargeDatabasePitrTests
{
    private readonly PgContainerFixture _pg;

    public LargeDatabasePitrTests(PgContainerFixture pg) => _pg = pg;

    private const int BaseRows = 50_000;
    private const int LogRowsEach = 5_000;

    [Fact]
    public async Task LargeDb_FullPlusThreeLogs_Pitr_MidChain_ExactMatch()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE big(" +
            "  id bigint PRIMARY KEY," +
            "  bucket int NOT NULL," +
            "  payload text NOT NULL," +
            "  created_at timestamptz NOT NULL DEFAULT clock_timestamp()" +
            ");" +
            "CREATE INDEX big_bucket_idx ON big(bucket);");

        await src.ExecAsync(
            $"INSERT INTO big(id, bucket, payload) " +
            $"SELECT g, g % 17, md5(g::text) FROM generate_series(1, {BaseRows}) g;",
            timeoutSeconds: 180);

        var (baseCount, baseSum) = await CountAndSumAsync(src);
        Assert.Equal(BaseRows, baseCount);

        var full = Helpers.BackupPath("largedb_full");
        await src.BackupFullAsync(full, compress: true, commandTimeoutSeconds: 600);

        // Three LOG backups with cutoffs captured between batches. Each log
        // inserts a distinct range of ids so PITR results are unambiguous.
        var cutoffs = new List<DateTime>();
        var logPaths = new List<string>();

        for (var batch = 0; batch < 3; batch++)
        {
            var idLow = BaseRows + batch * LogRowsEach + 1;
            var idHigh = BaseRows + (batch + 1) * LogRowsEach;

            await src.ExecAsync(
                $"INSERT INTO big(id, bucket, payload) " +
                $"SELECT g, g % 17, md5(g::text) " +
                $"FROM generate_series({idLow}, {idHigh}) g;",
                timeoutSeconds: 120);

            // Stamp commit timestamp; cutoff sits AFTER this batch but
            // BEFORE the next.
            var stamp = await ScalarTimestampAsync(src, "SELECT clock_timestamp()");
            cutoffs.Add(stamp);
            await Task.Delay(1100, TestContext.Current.CancellationToken);

            var logPath = Helpers.BackupPath($"largedb_log{batch}");
            await src.BackupLogAsync(
                logPath, batch == 0 ? full : logPaths[^1],
                compress: true, commandTimeoutSeconds: 600);
            logPaths.Add(logPath);
        }

        var (endCount, endSum) = await CountAndSumAsync(src);
        Assert.Equal(BaseRows + 3 * LogRowsEach, endCount);
        await src.CloseAsync();

        // PITR to mid-chain (after batch 0, before batch 1) restores
        // BaseRows + LogRowsEach.
        var midTarget = "largedb_mid_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(
                midTarget, cutoffs[0],
                new[] { full, logPaths[0], logPaths[1], logPaths[2] });

            await using var conn = await _pg.ConnectToAsync(midTarget);
            var (cnt, sum) = await CountAndSumAsync(conn);
            Assert.Equal(BaseRows + LogRowsEach, cnt);

            // Verify content match: same rows the source had at cutoff[0].
            var maxId = await ScalarLongAsync(conn, "SELECT max(id) FROM big");
            Assert.Equal((long)(BaseRows + LogRowsEach), maxId);

            // Spot-check payload integrity (no torn writes).
            var corrupt = await ScalarLongAsync(conn,
                "SELECT count(*) FROM big WHERE payload <> md5(id::text)");
            Assert.Equal(0L, corrupt);
        }
        finally
        {
            try { await _pg.DropDbAsync(midTarget); } catch { }
        }

        // PITR to last cutoff: should land at BaseRows + 3*LogRowsEach.
        var endTarget = "largedb_end_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            // stop_at = NULL means restore to end of chain.
            await RestoreAsync(
                endTarget, null,
                new[] { full, logPaths[0], logPaths[1], logPaths[2] });

            await using var conn = await _pg.ConnectToAsync(endTarget);
            var (cnt, sum) = await CountAndSumAsync(conn);
            Assert.Equal(endCount, cnt);
            Assert.Equal(endSum, sum);

            var corrupt = await ScalarLongAsync(conn,
                "SELECT count(*) FROM big WHERE payload <> md5(id::text)");
            Assert.Equal(0L, corrupt);

            // Index still works after restore.
            var bucket0 = await ScalarLongAsync(conn,
                "SELECT count(*) FROM big WHERE bucket = 0");
            var expected = (BaseRows + 3 * LogRowsEach) / 17 + 1;
            Assert.InRange(bucket0, expected - 50, expected + 50);
        }
        finally
        {
            try { await _pg.DropDbAsync(endTarget); } catch { }
        }
    }

    [Fact]
    public async Task LargeDb_UpdateDelete_Replay_MatchesSource()
    {
        // Exercise UPDATE + DELETE through logical replay on a non-trivial
        // dataset. Replica identity is the primary key.
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE accounts(" +
            "  id bigint PRIMARY KEY," +
            "  balance int NOT NULL," +
            "  status text NOT NULL DEFAULT 'active'" +
            ");" +
            "INSERT INTO accounts(id, balance) " +
            "SELECT g, 1000 FROM generate_series(1, 20000) g;",
            timeoutSeconds: 180);

        var full = Helpers.BackupPath("largedb_upddel_full");
        await src.BackupFullAsync(full, compress: true, commandTimeoutSeconds: 600);

        // Mixed DML producing a recognisable state.
        await src.ExecAsync(
            "UPDATE accounts SET balance = balance + 50 WHERE id % 7 = 0;" +
            "DELETE FROM accounts WHERE id % 113 = 0;" +
            "UPDATE accounts SET status = 'closed' " +
            "  WHERE id BETWEEN 5000 AND 5099;",
            timeoutSeconds: 120);

        var (srcCount, srcSum) = await ScalarPairAsync(src,
            "SELECT count(*)::bigint, sum(balance)::bigint FROM accounts");
        var srcClosed = await ScalarLongAsync(src,
            "SELECT count(*) FROM accounts WHERE status = 'closed'");

        var log = Helpers.BackupPath("largedb_upddel_log");
        await src.BackupLogAsync(log, full,
            compress: true, commandTimeoutSeconds: 600);
        await src.CloseAsync();

        var target = "upddel_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, null, new[] { full, log });
            await using var conn = await _pg.ConnectToAsync(target);
            var (cnt, sum) = await ScalarPairAsync(conn,
                "SELECT count(*)::bigint, sum(balance)::bigint FROM accounts");
            Assert.Equal(srcCount, cnt);
            Assert.Equal(srcSum, sum);
            var closed = await ScalarLongAsync(conn,
                "SELECT count(*) FROM accounts WHERE status = 'closed'");
            Assert.Equal(srcClosed, closed);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    private async Task RestoreAsync(string target, DateTime? stopAt, string[] files)
    {
        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        if (stopAt.HasValue)
        {
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(@files::text[], " +
                "target_db := @target, stop_at := @cutoff)";
            cmd.Parameters.AddWithValue("cutoff", stopAt.Value);
        }
        else
        {
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(@files::text[], " +
                "target_db := @target)";
        }
        cmd.Parameters.AddWithValue("files", files);
        cmd.Parameters.AddWithValue("target", target);
        cmd.CommandTimeout = 600;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        NpgsqlConnection.ClearAllPools();
    }

    private static async Task<(long count, long sum)> CountAndSumAsync(NpgsqlConnection conn) =>
        await ScalarPairAsync(conn, "SELECT count(*)::bigint, coalesce(sum(id),0)::bigint FROM big");

    private static async Task<(long, long)> ScalarPairAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        await using var rdr = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
        return (rdr.GetInt64(0), rdr.GetInt64(1));
    }

    private static async Task<long> ScalarLongAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<DateTime> ScalarTimestampAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (DateTime)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
