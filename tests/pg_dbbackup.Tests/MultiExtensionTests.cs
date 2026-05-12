using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class MultiExtensionTests
{
    private readonly PgWithExtensionsFixture _pg;

    public MultiExtensionTests(PgWithExtensionsFixture pg) => _pg = pg;

    private async Task<string> RestoreFreshAsync(string path)
    {
        var target = "multi_restored_" + Guid.NewGuid().ToString("N")[..8];
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
    public async Task Combined_Pgvector_And_PgTextsearch_RoundTrip()
    {
        if (!_pg.HasPgvector || !_pg.HasPgTextsearch)
        {
            Assert.Skip("pgvector and/or pg_textsearch extension not available in container");
        }

        await using var src = await _pg.CreateFreshDbWithExtensionAsync(
            "vector", "pg_textsearch");

        await src.ExecAsync(
            "CREATE TABLE notes(" +
            "  id int PRIMARY KEY," +
            "  body text NOT NULL," +
            "  embedding vector(3) NOT NULL);", timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO notes VALUES " +
            "(1, 'the quick brown fox',         '[1,0,0]')," +
            "(2, 'the slow blue whale',         '[0,1,0]')," +
            "(3, 'searchable text content',     '[0,0,1]')," +
            "(4, 'another searchable example',  '[1,1,0]');", timeoutSeconds: 60);
        await src.ExecAsync(
            "CREATE INDEX notes_bm25 ON notes USING bm25 (body) " +
            "WITH (text_config = 'english');", timeoutSeconds: 60);

        var path = Helpers.BackupPath("multi");
        await src.BackupFullAsync(path, compress: true, commandTimeoutSeconds: 180);
        await src.CloseAsync();

        var target = await RestoreFreshAsync(path);
        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var rconn = await _pg.ConnectToAsync(target);

            await using (var c = rconn.CreateCommand())
            {
                c.CommandText = "SELECT count(*) FROM notes";
                Assert.Equal(4L, (long)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            await using (var c = rconn.CreateCommand())
            {
                c.CommandText =
                    "SELECT id FROM notes ORDER BY embedding <-> '[1,0,0]'::vector LIMIT 1";
                Assert.Equal(1, (int)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            await using (var c = rconn.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM (" +
                    "  SELECT id FROM notes " +
                    "  ORDER BY body <@> 'searchable'::bm25query ASC LIMIT 4" +
                    ") s WHERE s.id IN (3, 4)";
                Assert.True((long)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))! >= 1L,
                    "BM25 top results should include docs with 'searchable'");
            }

            await using (var c = rconn.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM pg_extension " +
                    "WHERE extname IN ('vector','pg_textsearch')";
                Assert.Equal(2L, (long)(await c.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            await _pg.DropDbAsync(target);
        }
    }
}
