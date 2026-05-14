using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Array of a user-defined composite type as a column. Composite types
/// auto-generate an array type alongside the row type; the backup must
/// preserve both and the column's reference to the array type.
/// </summary>
public sealed class ArrayOfCompositeTests
{
    private readonly PgContainerFixture _pg;

    public ArrayOfCompositeTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Array_Of_Composite_Column_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TYPE tag AS (k text, v text);" +
            "CREATE TABLE aoc_t(id int PRIMARY KEY, tags tag[] NOT NULL);" +
            "INSERT INTO aoc_t VALUES " +
            "  (1, ARRAY[ROW('env','prod')::tag, ROW('tier','db')::tag]), " +
            "  (2, ARRAY[]::tag[]);");

        var full = Helpers.BackupPath("aoc");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "aoc_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            // Element access works.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText = "SELECT (tags[1]).v FROM aoc_t WHERE id = 1";
                Assert.Equal("prod", (string)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // Cardinality preserved. cardinality(empty) = 0, not NULL.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT cardinality(tags) FROM aoc_t ORDER BY id";
                await using var rdr = await cmd.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
                var vals = new List<int?>();
                while (await rdr.ReadAsync(TestContext.Current.CancellationToken))
                    vals.Add(rdr.IsDBNull(0) ? null : rdr.GetInt32(0));
                Assert.Equal(new int?[] { 2, 0 }, vals.ToArray());
            }
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
