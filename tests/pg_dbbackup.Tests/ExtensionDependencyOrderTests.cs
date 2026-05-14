using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Tables that depend on extension types (e.g., citext, hstore) must
/// have the extension created BEFORE the table DDL. A naive DDL
/// ordering would emit CREATE TABLE before CREATE EXTENSION and fail
/// with "type does not exist".
/// </summary>
public sealed class ExtensionDependencyOrderTests
{
    private readonly PgContainerFixture _pg;

    public ExtensionDependencyOrderTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Table_Using_Citext_RoundTrips_With_Extension()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE EXTENSION citext;" +
            "CREATE TABLE edo_t(id int PRIMARY KEY, email citext UNIQUE);" +
            "INSERT INTO edo_t VALUES (1, 'Hello@Example.COM'), (2, 'a@b.co');");

        var full = Helpers.BackupPath("edo");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "edo_" + Guid.NewGuid().ToString("N")[..8];
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

            // Extension present.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM pg_extension WHERE extname = 'citext'";
                Assert.Equal(1L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // citext semantics still work: case-insensitive equality.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM edo_t " +
                    "WHERE email = 'HELLO@EXAMPLE.COM'";
                Assert.Equal(1L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // UNIQUE constraint still enforces case-insensitively.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "INSERT INTO edo_t VALUES (99, 'hello@example.com')";
                var ex = await Assert.ThrowsAsync<PostgresException>(
                    () => c.ExecuteNonQueryAsync(
                        TestContext.Current.CancellationToken));
                Assert.Equal("23505", ex.SqlState);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
