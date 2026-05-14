using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class IndexAndStatisticsTests
{
    private readonly PgContainerFixture _pg;

    public IndexAndStatisticsTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Index_Variants_RoundTrip_Through_Simple_Full()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE EXTENSION IF NOT EXISTS pg_trgm;" +
            "CREATE TABLE idx_matrix(" +
            "  id int PRIMARY KEY," +
            "  active boolean NOT NULL," +
            "  name text NOT NULL," +
            "  upper_name text NOT NULL DEFAULT ''," +
            "  bucket int NOT NULL," +
            "  doc jsonb NOT NULL," +
            "  loc point NOT NULL DEFAULT '(0,0)'" +
            ");" +
            "INSERT INTO idx_matrix(id, active, name, upper_name, bucket, doc) " +
            "SELECT g, g % 2 = 0, 'x' || g, upper('x' || g), g % 10, " +
            "       jsonb_build_object('k', g) " +
            "FROM generate_series(1, 50) g;" +
            "CREATE INDEX idx_partial ON idx_matrix(id) WHERE active;" +
            "CREATE INDEX idx_expr ON idx_matrix(lower(name));" +
            "CREATE INDEX idx_include ON idx_matrix(bucket) INCLUDE (name);" +
            "CREATE INDEX idx_brin ON idx_matrix USING BRIN(id);" +
            "CREATE INDEX idx_spgist ON idx_matrix USING SPGIST(loc);" +
            "CREATE INDEX idx_gin_jsonb ON idx_matrix USING GIN(doc jsonb_path_ops);" +
            "CREATE INDEX idx_gin_trgm ON idx_matrix USING GIN(name gin_trgm_ops);");

        var full = Helpers.BackupPath("idxvar");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "idxvar_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT indexname, indexdef FROM pg_indexes " +
                "WHERE tablename = 'idx_matrix' ORDER BY indexname";
            await using var rdr = await cmd.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            var defs = new Dictionary<string, string>();
            while (await rdr.ReadAsync(TestContext.Current.CancellationToken))
                defs[rdr.GetString(0)] = rdr.GetString(1);

            Assert.Contains("idx_partial", defs.Keys);
            Assert.Contains("WHERE active", defs["idx_partial"]);
            Assert.Contains("idx_expr", defs.Keys);
            Assert.Contains("lower(name)", defs["idx_expr"]);
            Assert.Contains("idx_include", defs.Keys);
            Assert.Contains("INCLUDE", defs["idx_include"]);
            Assert.Contains("idx_brin", defs.Keys);
            Assert.Contains("USING brin", defs["idx_brin"]);
            Assert.Contains("idx_spgist", defs.Keys);
            Assert.Contains("USING spgist", defs["idx_spgist"]);
            Assert.Contains("idx_gin_jsonb", defs.Keys);
            Assert.Contains("jsonb_path_ops", defs["idx_gin_jsonb"]);
            Assert.Contains("idx_gin_trgm", defs.Keys);
            Assert.Contains("gin_trgm_ops", defs["idx_gin_trgm"]);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Statistics_Object_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE stat_t(a int NOT NULL, b int NOT NULL, c int NOT NULL, " +
            "  PRIMARY KEY(a, b));" +
            "CREATE STATISTICS stat_t_ab (ndistinct, dependencies) " +
            "  ON a, b FROM stat_t;");

        var full = Helpers.BackupPath("stats");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "stats_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT count(*) FROM pg_statistic_ext WHERE stxname = 'stat_t_ab'";
            var n = (long)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
            Assert.Equal(1L, n);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Icu_Collation_Column_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE COLLATION ci_und (provider = 'icu', locale = 'und-u-ks-level2', " +
            "  deterministic = false);" +
            "CREATE TABLE icu_t(id int PRIMARY KEY, label text COLLATE ci_und NOT NULL);" +
            "INSERT INTO icu_t VALUES (1, 'foo'), (2, 'FOO');");

        var full = Helpers.BackupPath("icu");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "icu_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT count(*) FROM icu_t WHERE label = 'foo'";
            var n = (long)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
            Assert.Equal(2L, n);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    private async Task RestoreAsync(string target, string file)
    {
        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
        cmd.Parameters.AddWithValue("files", new[] { file });
        cmd.Parameters.AddWithValue("tgt", target);
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        NpgsqlConnection.ClearAllPools();
    }
}
