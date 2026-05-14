using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Two schemas containing tables with identical names, plus a cross-schema
/// FOREIGN KEY between them. Tests that the backup correctly schema-
/// qualifies references so the restored database keeps both tables and
/// the FK pointing across schemas.
/// </summary>
public sealed class SchemaCollisionTests
{
    private readonly PgContainerFixture _pg;

    public SchemaCollisionTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Same_Tablename_Two_Schemas_With_CrossSchema_Fk_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE SCHEMA s_a;" +
            "CREATE SCHEMA s_b;" +
            "CREATE TABLE s_a.t(id int PRIMARY KEY, label text);" +
            "CREATE TABLE s_b.t(" +
            "  id int PRIMARY KEY, " +
            "  a_id int NOT NULL REFERENCES s_a.t(id));" +
            "INSERT INTO s_a.t VALUES (1,'in-a');" +
            "INSERT INTO s_b.t VALUES (1,1);");

        var full = Helpers.BackupPath("schema_col");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "schema_col_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            // Both tables exist and FK targets s_a.t.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT conname, " +
                    "       (confrelid::regclass)::text AS ref " +
                    "FROM pg_constraint " +
                    "WHERE conrelid = 's_b.t'::regclass AND contype = 'f'";
                await using var rdr = await cmd.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
                Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
                Assert.Equal("s_a.t", rdr.GetString(1));
            }

            // Row data preserved.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText = "SELECT label FROM s_a.t WHERE id = 1";
                Assert.Equal("in-a", (string)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // FK enforced: inserting orphan into s_b.t must fail.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO s_b.t VALUES (99, 999)";
                var ex = await Assert.ThrowsAsync<PostgresException>(
                    () => cmd.ExecuteNonQueryAsync(
                        TestContext.Current.CancellationToken));
                Assert.Equal("23503", ex.SqlState);
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
