using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class TimescaleDbTests
{
    private readonly PgWithExtensionsFixture _pg;

    public TimescaleDbTests(PgWithExtensionsFixture pg) => _pg = pg;

    private Task<string> RestoreFreshAsync(params string[] paths) =>
        RestoreFreshAsync(null, paths);

    private async Task<string> RestoreFreshAsync(DateTime? stopAt, params string[] paths)
    {
        var target = "tsdb_restored_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText = stopAt.HasValue
            ? "SELECT dbbackup.pg_dbrestore(@db, @files::text[], target_db := @tgt, stop_at := @stop)"
            : "SELECT dbbackup.pg_dbrestore(@db, @files::text[], target_db := @tgt)";
        cmd.Parameters.AddWithValue("db", "ignored");
        cmd.Parameters.AddWithValue("files", paths);
        cmd.Parameters.AddWithValue("tgt", target);
        if (stopAt.HasValue)
            cmd.Parameters.AddWithValue("stop", stopAt.Value);
        cmd.CommandTimeout = 300;
        try
        {
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        catch (Exception ex)
        {
            var logs = await _pg.LogsAsync();
            throw new InvalidOperationException(
                "Timescale restore failed. PostgreSQL logs:\n" + logs, ex);
        }
        return target;
    }

    [Fact]
    public async Task Timescaledb_Hypertable_Backup_Produces_Bak()
    {
        if (!_pg.HasTimescaleDb)
        {
            Assert.Skip("timescaledb extension not available in container");
        }

        await using var src = await _pg.CreateFreshDbWithExtensionAsync("timescaledb");
        await src.ExecAsync(
            "CREATE TABLE sensor_data(" +
            "  ts timestamptz NOT NULL," +
            "  device_id int NOT NULL," +
            "  value double precision NOT NULL);", timeoutSeconds: 60);
        await src.ExecAsync(
            "SELECT create_hypertable('sensor_data', 'ts', chunk_time_interval => INTERVAL '1 day');",
            timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO sensor_data " +
            "SELECT '2025-01-01'::timestamptz + (g * INTERVAL '2 hours'), " +
            "       (g % 5)::int, " +
            "       random()*100 " +
            "FROM generate_series(0, 99) g;", timeoutSeconds: 60);

        var path = Helpers.BackupPath("tsdb");
        await src.BackupFullAsync(path, compress: true, commandTimeoutSeconds: 300);

        await using var c = src.CreateCommand();
        c.CommandText = "SELECT (dbbackup.pg_dbbackup_verify(@p)).is_valid";
        c.Parameters.AddWithValue("p", path);
        var ok = (bool)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        Assert.True(ok, ".bak should verify after TimescaleDB hypertable backup");
    }

    [Fact]
    public async Task Timescaledb_Hypertable_Backup_Simple_RoundTrip()
    {
        if (!_pg.HasTimescaleDb)
        {
            Assert.Skip("timescaledb extension not available in container");
        }

        await using var src = await _pg.CreateFreshDbWithExtensionAsync("timescaledb");
        await src.ExecAsync(
            "CREATE TABLE sensor_data(" +
            "  ts timestamptz NOT NULL," +
            "  device_id int NOT NULL," +
            "  value double precision NOT NULL);", timeoutSeconds: 60);
        await src.ExecAsync(
            "SELECT create_hypertable('sensor_data', 'ts', chunk_time_interval => INTERVAL '1 day');",
            timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO sensor_data " +
            "SELECT '2025-01-01'::timestamptz + (g * INTERVAL '2 hours'), " +
            "       (g % 5)::int, " +
            "       random()*100 " +
            "FROM generate_series(0, 99) g;", timeoutSeconds: 60);

        var path = Helpers.BackupPath("tsdb");
        await src.BackupFullAsync(path, compress: true, commandTimeoutSeconds: 300);
        await src.CloseAsync();

        var target = await RestoreFreshAsync(path);
        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var rconn = await _pg.ConnectToAsync(target);

            await using (var cmd = rconn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM sensor_data";
                cmd.CommandTimeout = 60;
                Assert.Equal(100L, (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            await using (var cmd = rconn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM timescaledb_information.hypertables " +
                    "WHERE hypertable_name = 'sensor_data'";
                cmd.CommandTimeout = 60;
                Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            await using (var cmd = rconn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM timescaledb_information.chunks " +
                    "WHERE hypertable_name = 'sensor_data'";
                cmd.CommandTimeout = 60;
                var chunkCount = (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
                Assert.True(chunkCount > 0,
                    "expected at least one chunk after migrate_data");
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Timescaledb_Compressed_Chunk_RoundTrip()
    {
        if (!_pg.HasTimescaleDb)
        {
            Assert.Skip("timescaledb extension not available in container");
        }

        await using var src = await _pg.CreateFreshDbWithExtensionAsync("timescaledb");
        await src.ExecAsync(
            "CREATE TABLE sensor_data(" +
            "  ts timestamptz NOT NULL," +
            "  sensor_id int NOT NULL," +
            "  value double precision NOT NULL);", timeoutSeconds: 60);
        await src.ExecAsync(
            "SELECT create_hypertable('sensor_data', 'ts', chunk_time_interval => INTERVAL '1 day');",
            timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO sensor_data " +
            "SELECT '2025-01-01'::timestamptz + (g * INTERVAL '2 hours'), " +
            "       (g % 5)::int, " +
            "       random()*100 " +
            "FROM generate_series(0, 99) g;", timeoutSeconds: 60);
        await src.ExecAsync(
            "ALTER TABLE sensor_data SET (" +
            "  timescaledb.compress, " +
            "  timescaledb.compress_orderby = 'ts DESC', " +
            "  timescaledb.compress_segmentby = 'sensor_id');", timeoutSeconds: 60);
        await src.ExecAsync(
            "SELECT compress_chunk(c) FROM show_chunks('sensor_data') c;",
            timeoutSeconds: 120);

        long srcCompressed;
        await using (var cmd = src.CreateCommand())
        {
            cmd.CommandText =
                "SELECT count(*) FROM timescaledb_information.chunks " +
                "WHERE hypertable_name='sensor_data' AND is_compressed";
            cmd.CommandTimeout = 60;
            srcCompressed = (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        }
        Assert.True(srcCompressed > 0,
            "source must have at least one compressed chunk before backup");

        var path = Helpers.BackupPath("tsdb");
        await src.BackupFullAsync(path, compress: true, commandTimeoutSeconds: 300);
        await src.CloseAsync();

        var target = await RestoreFreshAsync(path);
        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var rconn = await _pg.ConnectToAsync(target);

            await using (var cmd = rconn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM sensor_data";
                cmd.CommandTimeout = 60;
                Assert.Equal(100L, (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            await using (var cmd = rconn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT compression_enabled FROM timescaledb_information.hypertables " +
                    "WHERE hypertable_name = 'sensor_data'";
                cmd.CommandTimeout = 60;
                var enabled = (bool)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
                Assert.True(enabled,
                    "restored hypertable must have compression enabled");
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Timescaledb_Multidimensional_Hypertable_RoundTrip()
    {
        if (!_pg.HasTimescaleDb)
        {
            Assert.Skip("timescaledb extension not available in container");
        }

        await using var src = await _pg.CreateFreshDbWithExtensionAsync("timescaledb");
        await src.ExecAsync(
            "CREATE TABLE sensor_multi(" +
            "  ts timestamptz NOT NULL," +
            "  device_id int NOT NULL," +
            "  value double precision NOT NULL);", timeoutSeconds: 60);
        await src.ExecAsync(
            "SELECT create_hypertable('sensor_multi', 'ts', chunk_time_interval => INTERVAL '1 day');" +
            "SELECT add_dimension('sensor_multi', by_hash('device_id', 4));",
            timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO sensor_multi " +
            "SELECT '2025-01-01'::timestamptz + (g * INTERVAL '2 hours'), " +
            "       (g % 8)::int, " +
            "       g::double precision " +
            "FROM generate_series(0, 99) g;", timeoutSeconds: 60);

        var path = Helpers.BackupPath("tsdb_multidim");
        await src.BackupFullAsync(path, compress: true, commandTimeoutSeconds: 300);
        await src.CloseAsync();

        var target = await RestoreFreshAsync(path);
        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var rconn = await _pg.ConnectToAsync(target);

            await using (var cmd = rconn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM sensor_multi";
                cmd.CommandTimeout = 60;
                Assert.Equal(100L, (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            await using (var cmd = rconn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) " +
                    "FROM _timescaledb_catalog.dimension d " +
                    "JOIN _timescaledb_catalog.hypertable h " +
                    "  ON h.id = d.hypertable_id " +
                    "WHERE h.table_name = 'sensor_multi'";
                cmd.CommandTimeout = 60;
                Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Timescaledb_Policies_RoundTrip()
    {
        if (!_pg.HasTimescaleDb)
        {
            Assert.Skip("timescaledb extension not available in container");
        }

        await using var src = await _pg.CreateFreshDbWithExtensionAsync("timescaledb");
        await src.ExecAsync(
            "CREATE TABLE policy_metrics(" +
            "  ts timestamptz NOT NULL," +
            "  device_id int NOT NULL," +
            "  value double precision NOT NULL);", timeoutSeconds: 60);
        await src.ExecAsync(
            "SELECT create_hypertable('policy_metrics', 'ts', chunk_time_interval => INTERVAL '1 day');" +
            "ALTER TABLE policy_metrics SET (timescaledb.compress);" +
            "SELECT add_retention_policy('policy_metrics', INTERVAL '30 days');" +
            "SELECT add_compression_policy('policy_metrics', INTERVAL '7 days');",
            timeoutSeconds: 120);
        await src.ExecAsync(
            "CREATE MATERIALIZED VIEW policy_metrics_hourly " +
            "WITH (timescaledb.continuous) AS " +
            "SELECT time_bucket('1 hour', ts) AS bucket, avg(value) AS avg_value " +
            "FROM policy_metrics GROUP BY bucket " +
            "WITH NO DATA;" +
            "SELECT add_continuous_aggregate_policy(" +
            "  'policy_metrics_hourly', " +
            "  start_offset => INTERVAL '7 days', " +
            "  end_offset => INTERVAL '1 hour', " +
            "  schedule_interval => INTERVAL '1 hour');",
            timeoutSeconds: 120);

        var path = Helpers.BackupPath("tsdb_policies");
        await src.BackupFullAsync(path, compress: true, commandTimeoutSeconds: 300);
        await src.CloseAsync();

        var target = await RestoreFreshAsync(path);
        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var rconn = await _pg.ConnectToAsync(target);

            await using (var cmd = rconn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM _timescaledb_config.bgw_job " +
                    "WHERE proc_name IN (" +
                    "  'policy_retention', " +
                    "  'policy_compression', " +
                    "  'policy_refresh_continuous_aggregate')";
                cmd.CommandTimeout = 60;
                Assert.Equal(3L, (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Timescaledb_Continuous_Aggregate_RoundTrip()
    {
        if (!_pg.HasTimescaleDb)
        {
            Assert.Skip("timescaledb extension not available in container");
        }

        await using var src = await _pg.CreateFreshDbWithExtensionAsync("timescaledb");
        await src.ExecAsync(
            "CREATE TABLE metrics(" +
            "  ts timestamptz NOT NULL," +
            "  v double precision NOT NULL);", timeoutSeconds: 60);
        await src.ExecAsync(
            "SELECT create_hypertable('metrics', 'ts', chunk_time_interval => INTERVAL '1 day');",
            timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO metrics " +
            "SELECT '2025-01-01'::timestamptz + (g * INTERVAL '1 hour'), random() " +
            "FROM generate_series(0, 99) g;", timeoutSeconds: 60);

        await src.ExecAsync(
            "CREATE MATERIALIZED VIEW metrics_hourly " +
            "WITH (timescaledb.continuous) AS " +
            "SELECT time_bucket('1 hour', ts) AS bucket, avg(v) AS avg_v " +
            "FROM metrics GROUP BY bucket " +
            "WITH NO DATA;", timeoutSeconds: 120);
        await src.ExecAsync(
            "CALL refresh_continuous_aggregate('metrics_hourly', NULL, NULL);",
            timeoutSeconds: 120);

        long srcCaggRows;
        await using (var cmd = src.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM metrics_hourly";
            cmd.CommandTimeout = 60;
            srcCaggRows = (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        }
        Assert.True(srcCaggRows > 0,
            "source CAGG must contain rows before backup");

        var path = Helpers.BackupPath("tsdb");
        await src.BackupFullAsync(path, compress: true, commandTimeoutSeconds: 300);
        await src.CloseAsync();

        var target = await RestoreFreshAsync(path);
        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var rconn = await _pg.ConnectToAsync(target);

            await using (var cmd = rconn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM metrics";
                cmd.CommandTimeout = 60;
                Assert.Equal(100L, (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            await using (var cmd = rconn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM timescaledb_information.continuous_aggregates " +
                    "WHERE view_name = 'metrics_hourly'";
                cmd.CommandTimeout = 60;
                Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            await using (var cmd = rconn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM metrics_hourly";
                cmd.CommandTimeout = 60;
                Assert.Equal(srcCaggRows, (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Timescaledb_Extension_Version_Preserved()
    {
        if (!_pg.HasTimescaleDb)
        {
            Assert.Skip("timescaledb extension not available in container");
        }

        await using var src = await _pg.CreateFreshDbWithExtensionAsync("timescaledb");

        string? srcVersion;
        await using (var cmd = src.CreateCommand())
        {
            cmd.CommandText = "SELECT extversion FROM pg_extension WHERE extname = 'timescaledb'";
            srcVersion = (string?)await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }
        Assert.False(string.IsNullOrEmpty(srcVersion),
            "timescaledb extension must have a version in source");

        await src.ExecAsync(
            "CREATE TABLE metrics(" +
            "  ts timestamptz NOT NULL," +
            "  v double precision NOT NULL);", timeoutSeconds: 60);
        await src.ExecAsync(
            "SELECT create_hypertable('metrics', 'ts', chunk_time_interval => INTERVAL '7 days');",
            timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO metrics " +
            "SELECT '2025-01-01'::timestamptz + (g * INTERVAL '1 hour'), random() " +
            "FROM generate_series(0, 49) g;", timeoutSeconds: 60);

        var path = Helpers.BackupPath("tsdb");
        await src.BackupFullAsync(path, compress: true, commandTimeoutSeconds: 300);
        await src.CloseAsync();

        var target = await RestoreFreshAsync(path);
        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var rconn = await _pg.ConnectToAsync(target);
            await using var cmd = rconn.CreateCommand();
            cmd.CommandText = "SELECT extversion FROM pg_extension WHERE extname = 'timescaledb'";
            cmd.CommandTimeout = 60;
            var restoredVersion = (string?)await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            Assert.Equal(srcVersion, restoredVersion);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Timescaledb_FullLog_Restore_RoundTrip()
    {
        if (!_pg.HasTimescaleDb)
        {
            Assert.Skip("timescaledb extension not available in container");
        }

        await using var src = await _pg.CreateFreshDbWithExtensionAsync("timescaledb");
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE sensor_log(" +
            "  ts timestamptz NOT NULL," +
            "  device_id int NOT NULL," +
            "  value double precision NOT NULL," +
            "  PRIMARY KEY(ts, device_id));", timeoutSeconds: 60);
        await src.ExecAsync(
            "SELECT create_hypertable('sensor_log', 'ts', chunk_time_interval => INTERVAL '1 day');",
            timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO sensor_log " +
            "SELECT '2025-01-01'::timestamptz + (g * INTERVAL '1 hour'), " +
            "       (g % 5)::int, g::double precision " +
            "FROM generate_series(0, 23) g;", timeoutSeconds: 60);

        var fullPath = Helpers.BackupPath("tsdb_fulllog");
        await src.BackupFullAsync(fullPath, compress: true, commandTimeoutSeconds: 300);

        await src.ExecAsync(
            "INSERT INTO sensor_log " +
            "SELECT '2025-01-02'::timestamptz + (g * INTERVAL '1 hour'), " +
            "       (g % 5)::int, (100 + g)::double precision " +
            "FROM generate_series(0, 23) g;", timeoutSeconds: 60);

        var logPath = Helpers.BackupPath("tsdb_fulllog");
        await src.BackupLogAsync(logPath, fullPath, compress: true, commandTimeoutSeconds: 300);
        await src.CloseAsync();

        var target = await RestoreFreshAsync(fullPath, logPath);
        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var rconn = await _pg.ConnectToAsync(target);

            await using (var cmd = rconn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*), max(value) FROM sensor_log";
                cmd.CommandTimeout = 60;
                await using var rdr = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
                Assert.Equal(48L, rdr.GetInt64(0));
                Assert.Equal(123d, rdr.GetDouble(1));
            }

            await using (var cmd = rconn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM timescaledb_information.hypertables " +
                    "WHERE hypertable_name = 'sensor_log'";
                cmd.CommandTimeout = 60;
                Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Timescaledb_FullLog_Pitr_Stops_At_Timestamp()
    {
        if (!_pg.HasTimescaleDb)
        {
            Assert.Skip("timescaledb extension not available in container");
        }

        await using var src = await _pg.CreateFreshDbWithExtensionAsync("timescaledb");
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE metrics_log(" +
            "  ts timestamptz NOT NULL," +
            "  device_id int NOT NULL," +
            "  value double precision NOT NULL," +
            "  PRIMARY KEY(ts, device_id));", timeoutSeconds: 60);
        await src.ExecAsync(
            "SELECT create_hypertable('metrics_log', 'ts', chunk_time_interval => INTERVAL '1 day');",
            timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO metrics_log VALUES " +
            "('2025-02-01 00:00+00', 1, 1), " +
            "('2025-02-01 01:00+00', 1, 2);", timeoutSeconds: 60);

        var fullPath = Helpers.BackupPath("tsdb_pitr");
        await src.BackupFullAsync(fullPath, compress: true, commandTimeoutSeconds: 300);

        await Task.Delay(1200, TestContext.Current.CancellationToken);
        await src.ExecAsync(
            "INSERT INTO metrics_log VALUES " +
            "('2025-02-02 00:00+00', 1, 10);", timeoutSeconds: 60);

        DateTime cutoff;
        await using (var c = src.CreateCommand())
        {
            c.CommandText = "SELECT clock_timestamp()";
            cutoff = (DateTime)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        }

        await Task.Delay(1200, TestContext.Current.CancellationToken);
        await src.ExecAsync(
            "INSERT INTO metrics_log VALUES " +
            "('2025-02-03 00:00+00', 1, 99);", timeoutSeconds: 60);

        var logPath = Helpers.BackupPath("tsdb_pitr");
        await src.BackupLogAsync(logPath, fullPath, compress: true, commandTimeoutSeconds: 300);
        await src.CloseAsync();

        var target = await RestoreFreshAsync(cutoff, fullPath, logPath);
        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var rconn = await _pg.ConnectToAsync(target);
            await using var cmd = rconn.CreateCommand();
            cmd.CommandText = "SELECT count(*), max(value) FROM metrics_log";
            cmd.CommandTimeout = 60;
            await using var rdr = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(3L, rdr.GetInt64(0));
            Assert.Equal(10d, rdr.GetDouble(1));
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
