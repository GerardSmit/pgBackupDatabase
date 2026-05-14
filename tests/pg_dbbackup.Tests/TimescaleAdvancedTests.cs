using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class TimescaleAdvancedTests
{
    private readonly PgWithExtensionsFixture _pg;

    public TimescaleAdvancedTests(PgWithExtensionsFixture pg) => _pg = pg;

    private void SkipIfNoTimescale()
    {
        if (!_pg.HasTimescaleDb)
            Assert.Skip("timescaledb extension not available in container");
    }

    private async Task<string> RestoreFreshAsync(params string[] paths)
    {
        var target = "ts_adv_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
        cmd.Parameters.AddWithValue("files", paths);
        cmd.Parameters.AddWithValue("tgt", target);
        cmd.CommandTimeout = 600;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        NpgsqlConnection.ClearAllPools();
        return target;
    }

    [Fact]
    public async Task Hypertable_FullLog_Across_Many_Chunks()
    {
        SkipIfNoTimescale();
        await using var src = await _pg.CreateFreshDbWithExtensionAsync("timescaledb");
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE ts_many(" +
            "  ts timestamptz NOT NULL," +
            "  device_id int NOT NULL," +
            "  value double precision NOT NULL," +
            "  PRIMARY KEY(ts, device_id));", timeoutSeconds: 60);
        await src.ExecAsync(
            "SELECT create_hypertable('ts_many', 'ts', " +
            "  chunk_time_interval => INTERVAL '1 day');",
            timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO ts_many " +
            "SELECT '2025-01-01'::timestamptz + (g * INTERVAL '4 hours'), 1, g " +
            "FROM generate_series(0, 5) g;", timeoutSeconds: 60);

        var full = Helpers.BackupPath("tsm_full");
        await src.BackupFullAsync(full, compress: true, commandTimeoutSeconds: 300);

        for (int day = 1; day <= 10; day++)
        {
            await src.ExecAsync(
                $"INSERT INTO ts_many " +
                $"SELECT '2025-01-{day + 1:D2}'::timestamptz + (g * INTERVAL '4 hours'), 1, g + {day * 100} " +
                $"FROM generate_series(0, 5) g;", timeoutSeconds: 60);
        }

        var log = Helpers.BackupPath("tsm_log");
        await src.BackupLogAsync(log, full, compress: true, commandTimeoutSeconds: 300);
        await src.CloseAsync();

        var target = await RestoreFreshAsync(full, log);
        try
        {
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM ts_many";
            cmd.CommandTimeout = 120;
            Assert.Equal(66L, (long)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);

            await using var c2 = r.CreateCommand();
            c2.CommandText =
                "SELECT count(*) FROM timescaledb_information.chunks " +
                "WHERE hypertable_name = 'ts_many'";
            var chunks = (long)(await c2.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
            Assert.True(chunks >= 5, $"expected several chunks, got {chunks}");
        }
        finally { try { await _pg.DropDbAsync(target); } catch { } }
    }

    [Fact]
    public async Task Hypertable_Compress_During_Chain_Window()
    {
        SkipIfNoTimescale();
        await using var src = await _pg.CreateFreshDbWithExtensionAsync("timescaledb");
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE ts_cc(ts timestamptz NOT NULL, sid int NOT NULL, " +
            "  v double precision NOT NULL, PRIMARY KEY(ts, sid));",
            timeoutSeconds: 60);
        await src.ExecAsync(
            "SELECT create_hypertable('ts_cc', 'ts', " +
            "  chunk_time_interval => INTERVAL '1 day');", timeoutSeconds: 60);
        await src.ExecAsync(
            "ALTER TABLE ts_cc SET (timescaledb.compress, " +
            "  timescaledb.compress_segmentby = 'sid');", timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO ts_cc " +
            "SELECT '2025-01-01'::timestamptz + (g * INTERVAL '2 hours'), 1, g " +
            "FROM generate_series(0, 11) g;", timeoutSeconds: 60);

        var full = Helpers.BackupPath("tscc_full");
        await src.BackupFullAsync(full, compress: true, commandTimeoutSeconds: 300);

        await src.ExecAsync(
            "SELECT compress_chunk(c) FROM show_chunks('ts_cc') c;",
            timeoutSeconds: 180);
        await src.ExecAsync(
            "INSERT INTO ts_cc " +
            "SELECT '2025-01-02'::timestamptz + (g * INTERVAL '2 hours'), 1, g + 100 " +
            "FROM generate_series(0, 5) g;", timeoutSeconds: 60);

        var log = Helpers.BackupPath("tscc_log");
        await src.BackupLogAsync(log, full, compress: true, commandTimeoutSeconds: 300);
        await src.CloseAsync();

        var target = await RestoreFreshAsync(full, log);
        try
        {
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM ts_cc";
            cmd.CommandTimeout = 120;
            var n = (long)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
            Assert.True(n >= 12, $"expected ≥12 rows, got {n}");
        }
        finally { try { await _pg.DropDbAsync(target); } catch { } }
    }

    [Fact]
    public async Task AddDimension_After_Full_Replays_Through_Log()
    {
        SkipIfNoTimescale();
        await using var src = await _pg.CreateFreshDbWithExtensionAsync("timescaledb");
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE ts_dim(ts timestamptz NOT NULL, dev int NOT NULL, " +
            "  v double precision NOT NULL, PRIMARY KEY(ts, dev));",
            timeoutSeconds: 60);
        await src.ExecAsync(
            "SELECT create_hypertable('ts_dim', 'ts', " +
            "  chunk_time_interval => INTERVAL '1 day');", timeoutSeconds: 60);

        var full = Helpers.BackupPath("tsdim_full");
        await src.BackupFullAsync(full, compress: true, commandTimeoutSeconds: 300);

        await src.ExecAsync(
            "SELECT add_dimension('ts_dim', by_hash('dev', 4));",
            timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO ts_dim " +
            "SELECT '2025-02-01'::timestamptz + (g * INTERVAL '1 hour'), g % 8, g " +
            "FROM generate_series(0, 31) g;", timeoutSeconds: 60);

        var log = Helpers.BackupPath("tsdim_log");
        await src.BackupLogAsync(log, full, compress: true, commandTimeoutSeconds: 300);
        await src.CloseAsync();

        var target = await RestoreFreshAsync(full, log);
        try
        {
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT count(*) FROM _timescaledb_catalog.dimension d " +
                "JOIN _timescaledb_catalog.hypertable h ON h.id = d.hypertable_id " +
                "WHERE h.table_name = 'ts_dim'";
            cmd.CommandTimeout = 60;
            Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally { try { await _pg.DropDbAsync(target); } catch { } }
    }

    [Fact]
    public async Task ContinuousAggregate_Refresh_After_Restore()
    {
        SkipIfNoTimescale();
        await using var src = await _pg.CreateFreshDbWithExtensionAsync("timescaledb");
        await src.ExecAsync(
            "CREATE TABLE m(ts timestamptz NOT NULL, v double precision NOT NULL);",
            timeoutSeconds: 60);
        await src.ExecAsync(
            "SELECT create_hypertable('m', 'ts', " +
            "  chunk_time_interval => INTERVAL '1 day');", timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO m " +
            "SELECT '2025-01-01'::timestamptz + (g * INTERVAL '1 hour'), g " +
            "FROM generate_series(0, 47) g;", timeoutSeconds: 60);
        await src.ExecAsync(
            "CREATE MATERIALIZED VIEW m_hourly " +
            "WITH (timescaledb.continuous) AS " +
            "SELECT time_bucket('1 hour', ts) AS b, avg(v) FROM m GROUP BY b " +
            "WITH NO DATA;", timeoutSeconds: 120);

        var path = Helpers.BackupPath("tscagg");
        await src.BackupFullAsync(path, compress: true, commandTimeoutSeconds: 300);
        await src.CloseAsync();

        var target = await RestoreFreshAsync(path);
        try
        {
            await using var r = await _pg.ConnectToAsync(target);
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "CALL refresh_continuous_aggregate('m_hourly', NULL, NULL);";
                cmd.CommandTimeout = 180;
                await cmd.ExecuteNonQueryAsync(
                    TestContext.Current.CancellationToken);
            }
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM m_hourly";
                cmd.CommandTimeout = 60;
                Assert.Equal(48L, (long)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
        }
        finally { try { await _pg.DropDbAsync(target); } catch { } }
    }

    [Fact]
    public async Task Hierarchical_Cagg_RoundTrips()
    {
        SkipIfNoTimescale();
        await using var src = await _pg.CreateFreshDbWithExtensionAsync("timescaledb");
        await src.ExecAsync(
            "CREATE TABLE m(ts timestamptz NOT NULL, v double precision NOT NULL);",
            timeoutSeconds: 60);
        await src.ExecAsync(
            "SELECT create_hypertable('m', 'ts', " +
            "  chunk_time_interval => INTERVAL '1 day');", timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO m " +
            "SELECT '2025-01-01'::timestamptz + (g * INTERVAL '15 minutes'), g " +
            "FROM generate_series(0, 191) g;", timeoutSeconds: 60);
        await src.ExecAsync(
            "CREATE MATERIALIZED VIEW m_hourly " +
            "WITH (timescaledb.continuous) AS " +
            "SELECT time_bucket('1 hour', ts) AS b, avg(v) AS av FROM m GROUP BY b;",
            timeoutSeconds: 180);
        await src.ExecAsync(
            "CREATE MATERIALIZED VIEW m_daily " +
            "WITH (timescaledb.continuous) AS " +
            "SELECT time_bucket('1 day', b) AS d, avg(av) FROM m_hourly GROUP BY d;",
            timeoutSeconds: 180);

        var path = Helpers.BackupPath("tshier");
        await src.BackupFullAsync(path, compress: true, commandTimeoutSeconds: 300);
        await src.CloseAsync();

        var target = await RestoreFreshAsync(path);
        try
        {
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT count(*) FROM timescaledb_information.continuous_aggregates " +
                "WHERE view_name IN ('m_hourly', 'm_daily')";
            cmd.CommandTimeout = 60;
            Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally { try { await _pg.DropDbAsync(target); } catch { } }
    }

    [Fact]
    public async Task DropChunks_MidChain_Replays()
    {
        SkipIfNoTimescale();
        await using var src = await _pg.CreateFreshDbWithExtensionAsync("timescaledb");
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE m(ts timestamptz NOT NULL, v int NOT NULL, " +
            "  PRIMARY KEY(ts));", timeoutSeconds: 60);
        await src.ExecAsync(
            "SELECT create_hypertable('m', 'ts', " +
            "  chunk_time_interval => INTERVAL '1 day');", timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO m " +
            "SELECT '2025-01-01'::timestamptz + (g * INTERVAL '6 hours'), g " +
            "FROM generate_series(0, 23) g;", timeoutSeconds: 60);

        var full = Helpers.BackupPath("tsdrop_full");
        await src.BackupFullAsync(full, compress: true, commandTimeoutSeconds: 300);

        await src.ExecAsync(
            "SELECT drop_chunks('m', older_than => '2025-01-04'::timestamptz);",
            timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO m " +
            "SELECT '2025-01-10'::timestamptz + (g * INTERVAL '6 hours'), 9000 + g " +
            "FROM generate_series(0, 3) g;", timeoutSeconds: 60);

        var log = Helpers.BackupPath("tsdrop_log");
        await src.BackupLogAsync(log, full, compress: true, commandTimeoutSeconds: 300);
        await src.CloseAsync();

        var target = await RestoreFreshAsync(full, log);
        try
        {
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT count(*) FROM m WHERE ts >= '2025-01-10'";
            cmd.CommandTimeout = 60;
            Assert.Equal(4L, (long)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally { try { await _pg.DropDbAsync(target); } catch { } }
    }

    [Fact]
    public async Task User_Defined_Action_Job_RoundTrips()
    {
        SkipIfNoTimescale();
        await using var src = await _pg.CreateFreshDbWithExtensionAsync("timescaledb");
        await src.ExecAsync(
            "CREATE PROCEDURE udj_proc(job_id int, config jsonb) " +
            "LANGUAGE plpgsql AS $$ BEGIN NULL; END $$;");
        await src.ExecAsync(
            "SELECT add_job('udj_proc', '1 hour'::interval);",
            timeoutSeconds: 60);

        var full = Helpers.BackupPath("tsudj");
        await src.BackupFullAsync(full, compress: true, commandTimeoutSeconds: 300);
        await src.CloseAsync();

        var target = await RestoreFreshAsync(full);
        try
        {
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT count(*) FROM _timescaledb_config.bgw_job " +
                "WHERE proc_name = 'udj_proc'";
            cmd.CommandTimeout = 60;
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally { try { await _pg.DropDbAsync(target); } catch { } }
    }

    [Fact]
    public async Task OnConflict_Insert_Hypertable_Replays()
    {
        SkipIfNoTimescale();
        await using var src = await _pg.CreateFreshDbWithExtensionAsync("timescaledb");
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE m(ts timestamptz NOT NULL, sid int NOT NULL, " +
            "  v int NOT NULL, PRIMARY KEY(ts, sid));", timeoutSeconds: 60);
        await src.ExecAsync(
            "SELECT create_hypertable('m', 'ts', " +
            "  chunk_time_interval => INTERVAL '1 day');", timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO m VALUES " +
            "('2025-01-01 00:00+00', 1, 1), " +
            "('2025-01-01 01:00+00', 1, 2);", timeoutSeconds: 60);

        var full = Helpers.BackupPath("tsoc_full");
        await src.BackupFullAsync(full, compress: true, commandTimeoutSeconds: 300);

        await src.ExecAsync(
            "INSERT INTO m VALUES " +
            "('2025-01-01 00:00+00', 1, 99), " +
            "('2025-01-02 00:00+00', 1, 5) " +
            "ON CONFLICT (ts, sid) DO UPDATE SET v = EXCLUDED.v;",
            timeoutSeconds: 60);

        var log = Helpers.BackupPath("tsoc_log");
        await src.BackupLogAsync(log, full, compress: true, commandTimeoutSeconds: 300);
        await src.CloseAsync();

        var target = await RestoreFreshAsync(full, log);
        try
        {
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT v FROM m WHERE ts = '2025-01-01 00:00+00' AND sid = 1";
            cmd.CommandTimeout = 60;
            Assert.Equal(99, (int)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally { try { await _pg.DropDbAsync(target); } catch { } }
    }

    [Fact]
    public async Task Chunk_Exclusion_Works_PostRestore()
    {
        SkipIfNoTimescale();
        await using var src = await _pg.CreateFreshDbWithExtensionAsync("timescaledb");
        await src.ExecAsync(
            "CREATE TABLE m(ts timestamptz NOT NULL, v int NOT NULL);",
            timeoutSeconds: 60);
        await src.ExecAsync(
            "SELECT create_hypertable('m', 'ts', " +
            "  chunk_time_interval => INTERVAL '1 day');", timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO m " +
            "SELECT '2025-01-01'::timestamptz + (g * INTERVAL '6 hours'), g " +
            "FROM generate_series(0, 39) g;", timeoutSeconds: 60);

        var full = Helpers.BackupPath("tsplan");
        await src.BackupFullAsync(full, compress: true, commandTimeoutSeconds: 300);
        await src.CloseAsync();

        var target = await RestoreFreshAsync(full);
        try
        {
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "EXPLAIN (FORMAT TEXT) " +
                "SELECT count(*) FROM m " +
                "WHERE ts BETWEEN '2025-01-05' AND '2025-01-06'";
            cmd.CommandTimeout = 60;
            await using var rdr = await cmd.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            var lines = new List<string>();
            while (await rdr.ReadAsync(TestContext.Current.CancellationToken))
                lines.Add(rdr.GetString(0));
            var planText = string.Join("\n", lines);
            // Should not scan every chunk.
            var hits = 0;
            foreach (var l in lines)
                if (l.Contains("Seq Scan on _hyper") || l.Contains("Index"))
                    hits++;
            Assert.True(hits <= 3,
                $"expected chunk exclusion to limit scans, got plan:\n{planText}");
        }
        finally { try { await _pg.DropDbAsync(target); } catch { } }
    }

    [Fact]
    public async Task MoveChunk_MidChain_Replays_Or_Skips_Gracefully()
    {
        SkipIfNoTimescale();
        await using var src = await _pg.CreateFreshDbWithExtensionAsync("timescaledb");
        await src.SetModeFullAsync();
        var tsName = "ts_alt_" + Guid.NewGuid().ToString("N")[..8];
        var tsDir = "/tmp/" + tsName;
        await _pg.ShellAsync(
            $"mkdir -p {tsDir} && chown postgres:postgres {tsDir}");
        await src.ExecAsync(
            $"CREATE TABLESPACE {tsName} LOCATION '{tsDir}';",
            timeoutSeconds: 60);
        await src.ExecAsync(
            "CREATE TABLE m(ts timestamptz NOT NULL, v int NOT NULL, " +
            "  PRIMARY KEY(ts));", timeoutSeconds: 60);
        await src.ExecAsync(
            "SELECT create_hypertable('m', 'ts', " +
            "  chunk_time_interval => INTERVAL '1 day');", timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO m " +
            "SELECT '2025-01-01'::timestamptz + (g * INTERVAL '6 hours'), g " +
            "FROM generate_series(0, 11) g;", timeoutSeconds: 60);

        var full = Helpers.BackupPath("tsmv_full");
        await src.BackupFullAsync(full, compress: true, commandTimeoutSeconds: 300);

        await src.ExecAsync(
            "INSERT INTO m " +
            "SELECT '2025-01-10'::timestamptz + (g * INTERVAL '6 hours'), 100 + g " +
            "FROM generate_series(0, 3) g;", timeoutSeconds: 60);

        var log = Helpers.BackupPath("tsmv_log");
        await src.BackupLogAsync(log, full, compress: true, commandTimeoutSeconds: 300);
        await src.CloseAsync();

        var target = await RestoreFreshAsync(full, log);
        try
        {
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM m";
            cmd.CommandTimeout = 60;
            Assert.Equal(16L, (long)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
            try
            {
                await using var admin = await _pg.AdminAsync();
                await admin.ExecAsync($"DROP TABLESPACE IF EXISTS {tsName}");
            }
            catch { }
            await _pg.ShellAsync($"rm -rf {tsDir}");
        }
    }
}
