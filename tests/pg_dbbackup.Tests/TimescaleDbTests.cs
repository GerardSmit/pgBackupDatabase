using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class TimescaleDbTests
{
    private readonly PgWithExtensionsFixture _pg;

    public TimescaleDbTests(PgWithExtensionsFixture pg) => _pg = pg;

    private async Task<string> RestoreFreshAsync(string path)
    {
        var target = "tsdb_restored_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbrestore(@db, ARRAY[@p]::text[], target_db := @tgt)";
        cmd.Parameters.AddWithValue("db", "ignored");
        cmd.Parameters.AddWithValue("p", path);
        cmd.Parameters.AddWithValue("tgt", target);
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync();
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
        var ok = (bool)(await c.ExecuteScalarAsync())!;
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
                Assert.Equal(100L, (long)(await cmd.ExecuteScalarAsync())!);
            }

            await using (var cmd = rconn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM timescaledb_information.hypertables " +
                    "WHERE hypertable_name = 'sensor_data'";
                cmd.CommandTimeout = 60;
                Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
            }

            await using (var cmd = rconn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM timescaledb_information.chunks " +
                    "WHERE hypertable_name = 'sensor_data'";
                cmd.CommandTimeout = 60;
                var chunkCount = (long)(await cmd.ExecuteScalarAsync())!;
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
            srcCompressed = (long)(await cmd.ExecuteScalarAsync())!;
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
                Assert.Equal(100L, (long)(await cmd.ExecuteScalarAsync())!);
            }

            await using (var cmd = rconn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT compression_enabled FROM timescaledb_information.hypertables " +
                    "WHERE hypertable_name = 'sensor_data'";
                cmd.CommandTimeout = 60;
                var enabled = (bool)(await cmd.ExecuteScalarAsync())!;
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
            srcCaggRows = (long)(await cmd.ExecuteScalarAsync())!;
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
                Assert.Equal(100L, (long)(await cmd.ExecuteScalarAsync())!);
            }

            await using (var cmd = rconn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM timescaledb_information.continuous_aggregates " +
                    "WHERE view_name = 'metrics_hourly'";
                cmd.CommandTimeout = 60;
                Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
            }

            await using (var cmd = rconn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM metrics_hourly";
                cmd.CommandTimeout = 60;
                Assert.Equal(srcCaggRows, (long)(await cmd.ExecuteScalarAsync())!);
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
            srcVersion = (string?)await cmd.ExecuteScalarAsync();
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
            var restoredVersion = (string?)await cmd.ExecuteScalarAsync();
            Assert.Equal(srcVersion, restoredVersion);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
