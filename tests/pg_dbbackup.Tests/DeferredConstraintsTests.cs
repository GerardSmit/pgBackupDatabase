using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// DEFERRABLE INITIALLY DEFERRED constraints: the deferrability flags
/// must survive restore so SET CONSTRAINTS still works on the restored
/// database.
/// </summary>
public sealed class DeferredConstraintsTests
{
    private readonly PgContainerFixture _pg;

    public DeferredConstraintsTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Deferrable_Initially_Deferred_Fk_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE dc_parent(id int PRIMARY KEY);" +
            "CREATE TABLE dc_child(" +
            "  id int PRIMARY KEY, " +
            "  parent_id int NOT NULL REFERENCES dc_parent(id) " +
            "    DEFERRABLE INITIALLY DEFERRED);" +
            "INSERT INTO dc_parent VALUES (1),(2);" +
            "INSERT INTO dc_child VALUES (10,1),(20,2);");

        var full = Helpers.BackupPath("dc");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "dc_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT condeferrable, condeferred " +
                    "FROM pg_constraint c " +
                    "WHERE c.conrelid = 'dc_child'::regclass " +
                    "  AND c.contype = 'f'";
                await using var rdr = await cmd.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
                Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
                Assert.True(rdr.GetBoolean(0)); // DEFERRABLE
                Assert.True(rdr.GetBoolean(1)); // INITIALLY DEFERRED
            }

            // Exercise the deferred behavior: inserting a child first then
            // its parent inside the same tx must succeed.
            await using (var tx = await r.BeginTransactionAsync(
                TestContext.Current.CancellationToken))
            {
                await using (var cmd = r.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO dc_child VALUES (30, 3)";
                    await cmd.ExecuteNonQueryAsync(
                        TestContext.Current.CancellationToken);
                }
                await using (var cmd = r.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO dc_parent VALUES (3)";
                    await cmd.ExecuteNonQueryAsync(
                        TestContext.Current.CancellationToken);
                }
                await tx.CommitAsync(TestContext.Current.CancellationToken);
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
