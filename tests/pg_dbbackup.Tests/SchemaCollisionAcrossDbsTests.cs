using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Multiple non-public schemas with the same table name. DDL ordering
/// and DATA section per-relation routing must distinguish them by
/// (nspname, relname), not relname alone.
/// </summary>
public sealed class SchemaCollisionAcrossDbsTests
{
    private readonly PgContainerFixture _pg;

    public SchemaCollisionAcrossDbsTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Same_Table_Name_In_Three_Schemas_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE SCHEMA s1;" +
            "CREATE SCHEMA s2;" +
            "CREATE SCHEMA s3;" +
            "CREATE TABLE s1.t(id int PRIMARY KEY, tag text);" +
            "CREATE TABLE s2.t(id int PRIMARY KEY, tag text);" +
            "CREATE TABLE s3.t(id int PRIMARY KEY, tag text);" +
            "INSERT INTO s1.t VALUES (1, 's1-row');" +
            "INSERT INTO s2.t VALUES (2, 's2-row');" +
            "INSERT INTO s3.t VALUES (3, 's3-row');");

        var full = Helpers.BackupPath("schcol");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "sc_" + Guid.NewGuid().ToString("N")[..8];
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
            foreach (var (sch, id, tag) in new[] {
                ("s1", 1, "s1-row"),
                ("s2", 2, "s2-row"),
                ("s3", 3, "s3-row") })
            {
                await using var c = r.CreateCommand();
                c.CommandText = $"SELECT id, tag FROM {sch}.t";
                await using var rdr = await c.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
                Assert.True(await rdr.ReadAsync(
                    TestContext.Current.CancellationToken));
                Assert.Equal(id, rdr.GetInt32(0));
                Assert.Equal(tag, rdr.GetString(1));
                Assert.False(await rdr.ReadAsync(
                    TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
