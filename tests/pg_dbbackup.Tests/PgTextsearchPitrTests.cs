using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// FULL-mode (logical PITR) coverage for pg_textsearch. Existing
/// PgTextsearchTests covers only SIMPLE-mode FULL.
/// </summary>
public sealed class PgTextsearchPitrTests
{
    private readonly PgWithExtensionsFixture _pg;

    public PgTextsearchPitrTests(PgWithExtensionsFixture pg) => _pg = pg;

    [Fact]
    public async Task PgTextsearch_FullPlusLog_RoundTrip()
    {
        if (!_pg.HasPgTextsearch)
            Assert.Skip("pg_textsearch not available");

        await using var src = await _pg.CreateFreshDbWithExtensionAsync("pg_textsearch");
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE articles(id int PRIMARY KEY, body text NOT NULL);",
            timeoutSeconds: 60);
        await src.ExecAsync(
            "CREATE INDEX articles_bm25 ON articles USING bm25 (body) " +
            "WITH (text_config = 'english');", timeoutSeconds: 60);
        await src.ExecAsync(
            "INSERT INTO articles VALUES " +
            "(1, 'the quick brown fox'), " +
            "(2, 'a lazy dog sleeps in the sun');",
            timeoutSeconds: 60);

        var full = Helpers.BackupPath("pgts_full");
        await src.BackupFullAsync(full, compress: true, commandTimeoutSeconds: 180);

        await src.ExecAsync(
            "INSERT INTO articles VALUES " +
            "(3, 'postgres full text search rocks'), " +
            "(4, 'another lazy day in the office');" +
            "UPDATE articles SET body = body || ' updated' WHERE id = 1;",
            timeoutSeconds: 60);

        var log = Helpers.BackupPath("pgts_log");
        await src.BackupLogAsync(log, full, compress: true, commandTimeoutSeconds: 180);
        await src.CloseAsync();

        var target = "pgts_pitr_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using (var admin = await _pg.AdminAsync())
            await using (var cmd = admin.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbrestore(ARRAY[@p1, @p2]::text[], target_db := @tgt)";
                cmd.Parameters.AddWithValue("p1", full);
                cmd.Parameters.AddWithValue("p2", log);
                cmd.Parameters.AddWithValue("tgt", target);
                cmd.CommandTimeout = 300;
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            NpgsqlConnection.ClearAllPools();
            await using var conn = await _pg.ConnectToAsync(target);

            await using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT count(*) FROM articles";
                Assert.Equal(4L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            await using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT body FROM articles WHERE id = 1";
                var v = (string)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.EndsWith(" updated", v);
            }

            // BM25 index still queryable after replay.
            await using (var c = conn.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM (" +
                    "  SELECT id FROM articles " +
                    "  ORDER BY body <@> 'lazy'::bm25query ASC LIMIT 5" +
                    ") s";
                Assert.True((long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))! >= 1L);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
