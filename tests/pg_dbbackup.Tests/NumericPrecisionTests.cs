using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// High-precision numeric and money values must round-trip exactly.
/// Any lossy text-mode transport would corrupt these.
/// </summary>
public sealed class NumericPrecisionTests
{
    private readonly PgContainerFixture _pg;

    public NumericPrecisionTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Numeric_38_18_And_Money_RoundTrip_Exactly()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE np_t(" +
            "  id int PRIMARY KEY, " +
            "  big numeric(38,18) NOT NULL, " +
            "  amt money NOT NULL);" +
            "INSERT INTO np_t VALUES " +
            "  (1, 12345678901234567890.123456789012345678, " +
            "   '$1,234,567.89'::money), " +
            "  (2, -0.000000000000000001, '$0.01'::money), " +
            "  (3, 0, '$0.00'::money);");

        var full = Helpers.BackupPath("np");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "np_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT id, big::text, amt::text FROM np_t ORDER BY id";
            await using var rdr = await cmd.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            var rows = new List<(int id, string big, string amt)>();
            while (await rdr.ReadAsync(TestContext.Current.CancellationToken))
                rows.Add((rdr.GetInt32(0), rdr.GetString(1), rdr.GetString(2)));

            Assert.Equal(3, rows.Count);
            Assert.Equal("12345678901234567890.123456789012345678", rows[0].big);
            Assert.Equal("-0.000000000000000001", rows[1].big);
            Assert.Equal("0.000000000000000000", rows[2].big);
            Assert.Contains("1,234,567.89", rows[0].amt);
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
