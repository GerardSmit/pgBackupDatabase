using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Functions declared with RETURNS TABLE(...) syntax instead of OUT
/// parameters or RECORD. pg_get_function_arguments emits them
/// differently; round-trip must keep the table-shape semantic so
/// callers like SELECT * FROM fn() still work.
/// </summary>
public sealed class ReturnsTableFunctionTests
{
    private readonly PgContainerFixture _pg;

    public ReturnsTableFunctionTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Returns_Table_Function_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE FUNCTION rtf_pairs(n int) " +
            "RETURNS TABLE(k int, sq bigint) " +
            "LANGUAGE sql IMMUTABLE AS " +
            "  $$ SELECT g, (g::bigint * g::bigint) " +
            "     FROM generate_series(1, n) g $$;");

        var full = Helpers.BackupPath("rtf");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "rtf_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
            cmd.Parameters.AddWithValue("files", new[] { full });
            cmd.Parameters.AddWithValue("tgt", target);
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
            NpgsqlConnection.ClearAllPools();

            await using var r = await _pg.ConnectToAsync(target);

            // Table-shape semantic preserved.
            await using var c = r.CreateCommand();
            c.CommandText = "SELECT k, sq FROM rtf_pairs(5) ORDER BY k";
            await using var rdr = await c.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            var pairs = new List<(int k, long sq)>();
            while (await rdr.ReadAsync(TestContext.Current.CancellationToken))
                pairs.Add((rdr.GetInt32(0), rdr.GetInt64(1)));
            Assert.Equal(
                new[] { (1, 1L), (2, 4L), (3, 9L), (4, 16L), (5, 25L) },
                pairs.ToArray());
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
