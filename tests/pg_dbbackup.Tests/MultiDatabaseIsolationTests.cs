using System.Collections.Concurrent;
using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// 16 active per-database chains running concurrently. Verifies that
/// taking a large FULL + LOG chain on one database does not interfere
/// with the writes, chain endpoints, or row counts of the other 15
/// databases on the same cluster.
/// </summary>
public sealed class MultiDatabaseIsolationTests
{
    private readonly PgContainerFixture _pg;

    public MultiDatabaseIsolationTests(PgContainerFixture pg) => _pg = pg;

    private const int DbCount = 16;
    private const int BigBaseRows = 30_000;

    [Fact]
    public async Task Sixteen_Active_Dbs_Backup_Of_Big_Does_Not_Affect_Others()
    {
        var bigIdx = 0;
        var dbNames = new string[DbCount];
        for (var i = 0; i < DbCount; i++)
            dbNames[i] = $"multidb_{i:D2}_{Guid.NewGuid().ToString("N")[..6]}";

        try
        {
            // Phase 1: create 16 fresh DBs with extension, FULL mode, baseline FULL.
            var fullPaths = new string[DbCount];
            for (var i = 0; i < DbCount; i++)
            {
                await CreateDbAsync(dbNames[i]);
                await using var conn = await _pg.ConnectToAsync(dbNames[i]);
                await conn.ExecAsync("CREATE EXTENSION pg_dbbackup");
                await conn.SetModeFullAsync();
                await conn.ExecAsync(
                    "CREATE TABLE t(id bigint PRIMARY KEY, marker text NOT NULL);");

                if (i == bigIdx)
                {
                    await conn.ExecAsync(
                        $"INSERT INTO t(id, marker) " +
                        $"SELECT g, 'big-base-' || g " +
                        $"FROM generate_series(1, {BigBaseRows}) g;",
                        timeoutSeconds: 120);
                }
                else
                {
                    await conn.ExecAsync(
                        $"INSERT INTO t VALUES (1, 'db{i}-seed');");
                }

                fullPaths[i] = Helpers.BackupPath($"multidb_{i:D2}");
                await conn.BackupFullAsync(
                    fullPaths[i], compress: true, commandTimeoutSeconds: 300);
            }

            // Phase 2: launch worker per DB inserting incrementing rows.
            using var cts = new CancellationTokenSource();
            var counts = new ConcurrentDictionary<int, long>();
            for (var i = 0; i < DbCount; i++)
                counts[i] = (i == bigIdx) ? BigBaseRows : 1L;

            var workers = new Task[DbCount];
            for (var i = 0; i < DbCount; i++)
            {
                var dbIdx = i;
                workers[i] = Task.Run(async () =>
                {
                    var startId = counts[dbIdx];
                    long next = startId + 1;
                    await using var conn = await _pg.ConnectToAsync(dbNames[dbIdx]);
                    while (!cts.IsCancellationRequested)
                    {
                        try
                        {
                            await using var cmd = conn.CreateCommand();
                            cmd.CommandText =
                                "INSERT INTO t(id, marker) " +
                                "SELECT g, 'db" + dbIdx + "-r' || g " +
                                "FROM generate_series(@from, @to) g";
                            cmd.Parameters.AddWithValue("from", next);
                            cmd.Parameters.AddWithValue("to", next + 49);
                            await cmd.ExecuteNonQueryAsync(cts.Token);
                            next += 50;
                            counts[dbIdx] = next - 1;
                        }
                        catch (OperationCanceledException) { break; }
                        catch (PostgresException) { break; }
                        await Task.Delay(20, CancellationToken.None);
                    }
                });
            }

            // Let workers warm up so the big-DB LOG backups capture concurrent
            // commits in their decoding window.
            await Task.Delay(2000, TestContext.Current.CancellationToken);

            // Phase 3: take 3 LOG backups on the big DB while others write.
            var bigLogPaths = new List<string>();
            await using (var bigConn = await _pg.ConnectToAsync(dbNames[bigIdx]))
            {
                for (var batch = 0; batch < 3; batch++)
                {
                    await Task.Delay(1500, TestContext.Current.CancellationToken);
                    var p = Helpers.BackupPath($"multidb_big_log{batch}");
                    var basePath = batch == 0 ? fullPaths[bigIdx] : bigLogPaths[^1];
                    await bigConn.BackupLogAsync(
                        p, basePath, compress: true, commandTimeoutSeconds: 300);
                    bigLogPaths.Add(p);
                }
            }

            // Phase 4: stop workers, wait, take final LOG on each DB.
            cts.Cancel();
            await Task.WhenAll(workers);

            var finalLogs = new string[DbCount];
            var sourceCounts = new long[DbCount];
            for (var i = 0; i < DbCount; i++)
            {
                await using var conn = await _pg.ConnectToAsync(dbNames[i]);
                var p = Helpers.BackupPath($"multidb_final_{i:D2}");
                var basePath = (i == bigIdx) ? bigLogPaths[^1] : fullPaths[i];
                await conn.BackupLogAsync(
                    p, basePath, compress: true, commandTimeoutSeconds: 300);
                finalLogs[i] = p;

                sourceCounts[i] = await ScalarLongAsync(conn,
                    "SELECT count(*) FROM t");
            }

            // Phase 5: verify each DB's chain endpoint matches its slot.
            for (var i = 0; i < DbCount; i++)
            {
                await using var conn = await _pg.ConnectToAsync(dbNames[i]);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT lc.confirmed_lsn::text, rs.confirmed_flush_lsn::text " +
                    "FROM dbbackup.logical_chains lc " +
                    "JOIN pg_replication_slots rs ON rs.slot_name = lc.slot_name " +
                    "WHERE lc.db_oid = (SELECT oid FROM pg_database " +
                    "                   WHERE datname = current_database())";
                await using var rdr = await cmd.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
                Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken),
                    $"db {dbNames[i]} missing chain row");
                Assert.Equal(rdr.GetString(0), rdr.GetString(1));
            }

            // Phase 6: restore the big DB chain to a fresh target, verify
            // count matches source and content is uncontaminated.
            var bigTarget = "multidb_big_restored_" + Guid.NewGuid().ToString("N")[..8];
            var bigFiles = new List<string> { fullPaths[bigIdx] };
            bigFiles.AddRange(bigLogPaths);
            bigFiles.Add(finalLogs[bigIdx]);
            try
            {
                await using (var admin = await _pg.AdminAsync())
                await using (var cmd = admin.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
                    cmd.Parameters.AddWithValue("files", bigFiles.ToArray());
                    cmd.Parameters.AddWithValue("tgt", bigTarget);
                    cmd.CommandTimeout = 600;
                    await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                }
                NpgsqlConnection.ClearAllPools();

                await using var restored = await _pg.ConnectToAsync(bigTarget);
                var restoredCount = await ScalarLongAsync(restored,
                    "SELECT count(*) FROM t");
                Assert.Equal(sourceCounts[bigIdx], restoredCount);

                // Cross-contamination check: restored big DB must not contain
                // any other DB's marker prefix.
                for (var i = 1; i < DbCount; i++)
                {
                    var leak = await ScalarLongAsync(restored,
                        $"SELECT count(*) FROM t WHERE marker LIKE 'db{i}-%'");
                    Assert.Equal(0L, leak);
                }
            }
            finally
            {
                try { await _pg.DropDbAsync(bigTarget); } catch { }
            }

            // Phase 7: each of the other 15 DBs must still be live, intact,
            // and isolated. Check live row count matches what worker observed
            // and content does not mention any other DB.
            for (var i = 1; i < DbCount; i++)
            {
                await using var conn = await _pg.ConnectToAsync(dbNames[i]);
                var live = await ScalarLongAsync(conn, "SELECT count(*) FROM t");
                Assert.Equal(sourceCounts[i], live);

                for (var j = 0; j < DbCount; j++)
                {
                    if (j == i) continue;
                    var leak = await ScalarLongAsync(conn,
                        $"SELECT count(*) FROM t WHERE marker LIKE 'db{j}-%'");
                    Assert.Equal(0L, leak);
                }

                // Also verify that DB i can be restored independently from
                // its own FULL + final LOG, confirming its chain is sound.
                var t = $"multidb_t{i:D2}_" + Guid.NewGuid().ToString("N")[..6];
                try
                {
                    await using (var admin = await _pg.AdminAsync())
                    await using (var cmd = admin.CreateCommand())
                    {
                        cmd.CommandText =
                            "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
                        cmd.Parameters.AddWithValue("files",
                            new[] { fullPaths[i], finalLogs[i] });
                        cmd.Parameters.AddWithValue("tgt", t);
                        cmd.CommandTimeout = 300;
                        await cmd.ExecuteNonQueryAsync(
                            TestContext.Current.CancellationToken);
                    }
                    NpgsqlConnection.ClearAllPools();
                    await using var r = await _pg.ConnectToAsync(t);
                    var rc = await ScalarLongAsync(r, "SELECT count(*) FROM t");
                    Assert.Equal(sourceCounts[i], rc);
                }
                finally
                {
                    try { await _pg.DropDbAsync(t); } catch { }
                }
            }
        }
        finally
        {
            foreach (var name in dbNames)
            {
                if (name is null) continue;
                try { await _pg.DropDbAsync(name); } catch { }
            }
        }
    }

    private async Task CreateDbAsync(string name)
    {
        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{name}\"";
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> ScalarLongAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;
        return (long)(await cmd.ExecuteScalarAsync(
            TestContext.Current.CancellationToken))!;
    }
}
