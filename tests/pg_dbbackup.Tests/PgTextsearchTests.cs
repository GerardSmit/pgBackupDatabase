using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class PgTextsearchTests
{
    private readonly PgWithExtensionsFixture _pg;

    public PgTextsearchTests(PgWithExtensionsFixture pg) => _pg = pg;

    private async Task<string> RestoreFreshAsync(string path)
    {
        var target = "pgts_restored_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbrestore(@db, ARRAY[@p]::text[], target_db := @tgt)";
        cmd.Parameters.AddWithValue("db", "ignored");
        cmd.Parameters.AddWithValue("p", path);
        cmd.Parameters.AddWithValue("tgt", target);
        cmd.CommandTimeout = 180;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return target;
    }

    [Fact]
    public async Task PgTextsearch_Data_RoundTrip_Simple()
    {
        if (!_pg.HasPgTextsearch)
        {
            Assert.Skip("pg_textsearch extension not available in container");
        }

        await using var src = await _pg.CreateFreshDbWithExtensionAsync("pg_textsearch");
        await src.ExecAsync(
            "CREATE TABLE articles(" +
            "  id int PRIMARY KEY," +
            "  body text NOT NULL);", timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO articles VALUES " +
            "(1, 'the quick brown fox jumps over the lazy dog')," +
            "(2, 'postgres is a powerful open source database')," +
            "(3, 'full text search with bm25 ranking')," +
            "(4, 'the lazy dog sleeps in the sun');", timeoutSeconds: 60);
        await src.ExecAsync(
            "CREATE INDEX articles_bm25 ON articles USING bm25 (body) " +
            "WITH (text_config = 'english');", timeoutSeconds: 60);

        var path = Helpers.BackupPath("pgts");
        await src.BackupFullAsync(path, compress: true, commandTimeoutSeconds: 180);
        await src.CloseAsync();

        var target = await RestoreFreshAsync(path);
        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var rconn = await _pg.ConnectToAsync(target);

            await using (var c = rconn.CreateCommand())
            {
                c.CommandText = "SELECT count(*) FROM articles";
                Assert.Equal(4L, (long)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            await using (var c = rconn.CreateCommand())
            {
                c.CommandText =
                    "SELECT id FROM articles " +
                    "ORDER BY body <@> 'lazy'::bm25query ASC LIMIT 1";
                var topId = (int)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
                Assert.True(topId == 1 || topId == 4,
                    $"top BM25 match should be a doc containing 'lazy' (got id={topId})");
            }
        }
        finally
        {
            await _pg.DropDbAsync(target);
        }
    }

    [Fact]
    public async Task PgTextsearch_Bm25_Index_Rebuilt_On_Simple_Restore()
    {
        if (!_pg.HasPgTextsearch)
        {
            Assert.Skip("pg_textsearch extension not available in container");
        }

        await using var src = await _pg.CreateFreshDbWithExtensionAsync("pg_textsearch");
        await src.ExecAsync(
            "CREATE TABLE docs(" +
            "  id int PRIMARY KEY," +
            "  body text NOT NULL);", timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO docs SELECT g, 'document number ' || g || ' with searchable text' " +
            "FROM generate_series(1, 100) g;", timeoutSeconds: 60);
        await src.ExecAsync(
            "CREATE INDEX docs_bm25 ON docs USING bm25 (body) " +
            "WITH (text_config = 'english');", timeoutSeconds: 60);

        var path = Helpers.BackupPath("pgts");
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
                    "WHERE tablename = 'docs' AND indexname = 'docs_bm25'";
                Assert.Equal(1L, (long)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            await using (var c = rconn.CreateCommand())
            {
                c.CommandText =
                    "SELECT amname FROM pg_class c " +
                    "JOIN pg_am a ON c.relam = a.oid " +
                    "WHERE c.relname = 'docs_bm25'";
                var am = (string)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
                Assert.Equal("bm25", am);
            }

            await using (var c = rconn.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM (" +
                    "  SELECT id FROM docs " +
                    "  ORDER BY body <@> 'searchable'::bm25query ASC LIMIT 5" +
                    ") s";
                Assert.True((long)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))! > 0L,
                    "BM25 search should return matches after restore");
            }
        }
        finally
        {
            await _pg.DropDbAsync(target);
        }
    }
}
