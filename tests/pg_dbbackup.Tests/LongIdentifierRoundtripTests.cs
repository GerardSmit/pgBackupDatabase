using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// PostgreSQL truncates identifiers to NAMEDATALEN-1 (63 chars by
/// default). DDL generation must emit identifiers via quote_ident,
/// which preserves the (already-truncated) name. A 63-char table name
/// round-tripped through backup+restore must come back byte-for-byte.
/// </summary>
public sealed class LongIdentifierRoundtripTests
{
    private readonly PgContainerFixture _pg;

    public LongIdentifierRoundtripTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Max_Length_Identifiers_RoundTrip()
    {
        var longTable = new string('t', 63);
        var longCol = new string('c', 63);
        var longIdx = "i_" + new string('x', 61);

        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            $"CREATE TABLE \"{longTable}\"(" +
            $"  \"{longCol}\" int PRIMARY KEY," +
            $"  v text);" +
            $"CREATE INDEX \"{longIdx}\" ON \"{longTable}\"(v);" +
            $"INSERT INTO \"{longTable}\" VALUES (1, 'a'), (2, 'b');");

        var full = Helpers.BackupPath("longid");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "longid_" + Guid.NewGuid().ToString("N")[..8];
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
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM pg_class " +
                    "WHERE relname = @t AND relkind = 'r'";
                c.Parameters.AddWithValue("t", longTable);
                Assert.Equal(1L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM pg_class " +
                    "WHERE relname = @i AND relkind = 'i'";
                c.Parameters.AddWithValue("i", longIdx);
                Assert.Equal(1L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
            await using (var c = r.CreateCommand())
            {
                c.CommandText = $"SELECT count(*) FROM \"{longTable}\"";
                Assert.Equal(2L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
