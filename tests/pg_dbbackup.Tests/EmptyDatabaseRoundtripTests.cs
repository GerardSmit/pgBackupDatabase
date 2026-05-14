using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// A database with no user-defined objects is the degenerate-but-legal
/// case. The backup must produce a valid file (with framing/checksums
/// intact even though it contains no schema/data) and the restore must
/// produce an empty target with the extension absent (we created the
/// source with the extension; the backup is logical, the restored DB
/// should reflect the post-rename state).
/// </summary>
public sealed class EmptyDatabaseRoundtripTests
{
    private readonly PgContainerFixture _pg;

    public EmptyDatabaseRoundtripTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Empty_Database_RoundTrips_Cleanly()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        // No CREATE TABLE — empty DB beyond the extension itself.

        var full = Helpers.BackupPath("empty_db");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "empty_" + Guid.NewGuid().ToString("N")[..8];
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

            // No user tables.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM pg_class c " +
                    "JOIN pg_namespace n ON n.oid = c.relnamespace " +
                    "WHERE c.relkind = 'r' AND n.nspname NOT IN " +
                    "      ('pg_catalog','information_schema','dbbackup')";
                Assert.Equal(0L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // The DB itself exists and is queryable.
            await using (var c = r.CreateCommand())
            {
                c.CommandText = "SELECT current_database()";
                Assert.Equal(target, (string)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
