using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class PgvectorTests
{
    private readonly PgWithExtensionsFixture _pg;

    public PgvectorTests(PgWithExtensionsFixture pg) => _pg = pg;

    private async Task<string> RestoreFreshAsync(string path)
    {
        var target = "pgvec_restored_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbrestore(ARRAY[@p]::text[], target_db := @tgt)";
        cmd.Parameters.AddWithValue("db", "ignored");
        cmd.Parameters.AddWithValue("p", path);
        cmd.Parameters.AddWithValue("tgt", target);
        cmd.CommandTimeout = 180;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return target;
    }

    [Fact]
    public async Task Pgvector_Data_RoundTrip_Simple()
    {
        if (!_pg.HasPgvector)
        {
            Assert.Skip("vector (pgvector) extension not available in container");
        }

        await using var src = await _pg.CreateFreshDbWithExtensionAsync("vector");
        await src.ExecAsync(
            "CREATE TABLE items(id int PRIMARY KEY, embedding vector(3));", timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO items VALUES " +
            "(1, '[1,2,3]')," +
            "(2, '[4,5,6]')," +
            "(3, '[0,0,1]')," +
            "(4, '[10,10,10]');", timeoutSeconds: 60);

        var path = Helpers.BackupPath("pgvec");
        await src.BackupFullAsync(path, compress: true, commandTimeoutSeconds: 180);
        await src.CloseAsync();

        var target = await RestoreFreshAsync(path);
        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var rconn = await _pg.ConnectToAsync(target);

            await using (var c = rconn.CreateCommand())
            {
                c.CommandText = "SELECT count(*) FROM items";
                Assert.Equal(4L, (long)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            await using (var c = rconn.CreateCommand())
            {
                c.CommandText =
                    "SELECT id FROM items ORDER BY embedding <-> '[1,2,3]'::vector LIMIT 1";
                var nearestId = (int)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
                Assert.Equal(1, nearestId);
            }

            await using (var c = rconn.CreateCommand())
            {
                c.CommandText =
                    "SELECT embedding::text FROM items WHERE id = 2";
                var emb = (string)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
                Assert.Equal("[4,5,6]", emb);
            }
        }
        finally
        {
            await _pg.DropDbAsync(target);
        }
    }

    [Fact]
    public async Task Pgvector_Hnsw_Index_Rebuilt_On_Simple_Restore()
    {
        if (!_pg.HasPgvector)
        {
            Assert.Skip("vector (pgvector) extension not available in container");
        }

        await using var src = await _pg.CreateFreshDbWithExtensionAsync("vector");
        await src.ExecAsync(
            "CREATE TABLE docs(id int PRIMARY KEY, embedding vector(3));", timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO docs SELECT g, ARRAY[random(),random(),random()]::vector " +
            "FROM generate_series(1, 200) g;", timeoutSeconds: 60);
        await src.ExecAsync(
            "CREATE INDEX docs_hnsw ON docs USING hnsw (embedding vector_l2_ops);",
            timeoutSeconds: 60);

        var path = Helpers.BackupPath("pgvec");
        await src.BackupFullAsync(path, compress: true, commandTimeoutSeconds: 180);
        await src.CloseAsync();

        var target = await RestoreFreshAsync(path);
        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var rconn = await _pg.ConnectToAsync(target);

            await using (var c = rconn.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM pg_indexes " +
                    "WHERE tablename = 'docs' AND indexname = 'docs_hnsw'";
                Assert.Equal(1L, (long)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            await using (var c = rconn.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM (" +
                    "  SELECT id FROM docs " +
                    "  ORDER BY embedding <-> '[0.5,0.5,0.5]'::vector LIMIT 5" +
                    ") s";
                Assert.Equal(5L, (long)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            await _pg.DropDbAsync(target);
        }
    }

    [Fact]
    public async Task Pgvector_Ivfflat_Index_RoundTrip()
    {
        if (!_pg.HasPgvector)
        {
            Assert.Skip("vector (pgvector) extension not available in container");
        }

        await using var src = await _pg.CreateFreshDbWithExtensionAsync("vector");
        await src.ExecAsync(
            "CREATE TABLE items(id int PRIMARY KEY, embedding vector(3));", timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO items SELECT g, ARRAY[random(),random(),random()]::vector " +
            "FROM generate_series(1, 150) g;", timeoutSeconds: 60);
        await src.ExecAsync(
            "CREATE INDEX items_ivf ON items USING ivfflat (embedding vector_l2_ops) " +
            "WITH (lists = 10);", timeoutSeconds: 60);

        var path = Helpers.BackupPath("pgvec");
        await src.BackupFullAsync(path, compress: true, commandTimeoutSeconds: 180);
        await src.CloseAsync();

        var target = await RestoreFreshAsync(path);
        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var rconn = await _pg.ConnectToAsync(target);

            await using (var c = rconn.CreateCommand())
            {
                c.CommandText =
                    "SELECT indexdef FROM pg_indexes " +
                    "WHERE tablename = 'items' AND indexname = 'items_ivf'";
                var def = (string?)await c.ExecuteScalarAsync(TestContext.Current.CancellationToken);
                Assert.NotNull(def);
                Assert.Contains("ivfflat", def!);
            }

            await using (var c = rconn.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM (" +
                    "  SELECT id FROM items " +
                    "  ORDER BY embedding <-> '[0.5,0.5,0.5]'::vector LIMIT 3" +
                    ") s";
                Assert.Equal(3L, (long)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            await _pg.DropDbAsync(target);
        }
    }
}
