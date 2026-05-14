using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Source database created with an ICU collation (different from the
/// server template's libc default). Restored database must either keep
/// the ICU collation or fail with an actionable error — silently
/// downgrading the collation would change index ordering and break apps.
/// </summary>
public sealed class CrossLocaleRestoreTests
{
    private readonly PgContainerFixture _pg;

    public CrossLocaleRestoreTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task ICU_Collation_Round_Trips_Or_Errors_Clearly()
    {
        // PG 16+ supports per-column ICU collations on the default DB.
        // Create a column with an explicit ICU collation; the restore
        // must preserve the COLLATE clause so ORDER BY semantics match.
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();

        // Define ICU collation in the source DB.
        await src.ExecAsync(
            "CREATE COLLATION german_phonebook " +
            "  (provider = icu, locale = 'de-u-co-phonebk', " +
            "   deterministic = false);" +
            "CREATE TABLE locale_t(" +
            "  id int PRIMARY KEY," +
            "  name text COLLATE german_phonebook NOT NULL" +
            ");" +
            "INSERT INTO locale_t VALUES " +
            "  (1, 'Mueller'), (2, 'Müller'), (3, 'Mahler');");

        // Ordering under german_phonebook treats 'ü' = 'ue'.
        // Mueller (mü) sorts BEFORE Mahler (ma)? No: 'a' < 'u'. So order
        // is Mahler, Mueller, Müller (last two are equal under phonebk).
        var srcOrder = await ReadOrderedAsync(src);

        var full = Helpers.BackupPath("locale");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "locale_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            // Collation object must round-trip.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT collprovider FROM pg_collation " +
                    "WHERE collname = 'german_phonebook'";
                var prov = await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken);
                Assert.NotNull(prov);
                // 'i' = ICU provider.
                Assert.Equal("i", ((char)prov!).ToString());
            }

            // Column-level collation must be reattached.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT c.collname FROM pg_attribute a " +
                    "JOIN pg_class t ON t.oid = a.attrelid " +
                    "LEFT JOIN pg_collation c ON c.oid = a.attcollation " +
                    "WHERE t.relname = 'locale_t' AND a.attname = 'name'";
                Assert.Equal("german_phonebook",
                    (string)(await cmd.ExecuteScalarAsync(
                        TestContext.Current.CancellationToken))!);
            }

            // Sort behavior reproduces the source ordering.
            var tgtOrder = await ReadOrderedAsync(r);
            Assert.Equal(srcOrder, tgtOrder);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    private static async Task<List<int>> ReadOrderedAsync(NpgsqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM locale_t ORDER BY name, id";
        var ids = new List<int>();
        await using var rdr = await cmd.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        while (await rdr.ReadAsync(TestContext.Current.CancellationToken))
            ids.Add(rdr.GetInt32(0));
        return ids;
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
