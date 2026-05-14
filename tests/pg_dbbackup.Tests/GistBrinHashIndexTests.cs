using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Non-btree index access methods preserve their `am` after restore.
/// GIST on tsvector, BRIN on timestamptz, HASH on text. Each must be
/// recreated with the same access method.
/// </summary>
public sealed class GistBrinHashIndexTests
{
    private readonly PgContainerFixture _pg;

    public GistBrinHashIndexTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Gist_Brin_Hash_Indexes_Preserve_AccessMethod()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE gbh_t(" +
            "  id int PRIMARY KEY, " +
            "  ts timestamptz NOT NULL, " +
            "  doc tsvector NOT NULL, " +
            "  tag text NOT NULL);" +
            "CREATE INDEX gbh_gist ON gbh_t USING GIST (doc);" +
            "CREATE INDEX gbh_brin ON gbh_t USING BRIN (ts);" +
            "CREATE INDEX gbh_hash ON gbh_t USING HASH (tag);");

        var full = Helpers.BackupPath("gbh");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "gbh_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT i.relname, am.amname " +
                "FROM pg_class i " +
                "JOIN pg_am am ON am.oid = i.relam " +
                "WHERE i.relname IN ('gbh_gist','gbh_brin','gbh_hash') " +
                "ORDER BY i.relname";
            await using var rdr = await cmd.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            var got = new List<(string idx, string am)>();
            while (await rdr.ReadAsync(TestContext.Current.CancellationToken))
                got.Add((rdr.GetString(0), rdr.GetString(1)));

            Assert.Equal(3, got.Count);
            Assert.Contains(("gbh_brin", "brin"), got);
            Assert.Contains(("gbh_gist", "gist"), got);
            Assert.Contains(("gbh_hash", "hash"), got);
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
