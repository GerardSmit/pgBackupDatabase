using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// PostgreSQL 15+ revokes CREATE-on-public from PUBLIC by default. A
/// stricter hardened DB may also revoke USAGE. Backup must preserve
/// the schema ACL exactly so the restored DB doesn't silently widen
/// permissions.
/// </summary>
public sealed class RevokedPublicSchemaTests
{
    private readonly PgContainerFixture _pg;

    public RevokedPublicSchemaTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Revoked_Public_Schema_Permissions_RoundTrip()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "REVOKE CREATE ON SCHEMA public FROM PUBLIC;" +
            "REVOKE USAGE ON SCHEMA public FROM PUBLIC;" +
            "CREATE TABLE rps_t(id int PRIMARY KEY);" +
            "INSERT INTO rps_t VALUES (1);");

        var full = Helpers.BackupPath("rps");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "rps_" + Guid.NewGuid().ToString("N")[..8];
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

            // Table data round-trips.
            await using (var c = r.CreateCommand())
            {
                c.CommandText = "SELECT count(*) FROM rps_t";
                Assert.Equal(1L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // PUBLIC has no CREATE on schema public.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT has_schema_privilege('public', 'public', 'CREATE')";
                var canCreate = (bool)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.False(canCreate,
                    "PUBLIC must not have CREATE on schema public after restore");
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
