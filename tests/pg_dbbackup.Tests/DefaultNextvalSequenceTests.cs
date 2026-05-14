using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Column DEFAULT references a free-standing sequence via nextval.
/// DDL emission must preserve the dependency: the sequence DDL must
/// precede the table DDL, and the DEFAULT expression must rewire to
/// the restored sequence's OID. Sequence value must also be restored
/// so subsequent INSERTs continue without gap.
/// </summary>
public sealed class DefaultNextvalSequenceTests
{
    private readonly PgContainerFixture _pg;

    public DefaultNextvalSequenceTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Sequence_Backed_Default_Continues_After_Restore()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE SEQUENCE dns_seq START WITH 1000;" +
            "CREATE TABLE dns_t(" +
            "  id bigint NOT NULL DEFAULT nextval('dns_seq')," +
            "  v text" +
            ");" +
            "INSERT INTO dns_t(v) VALUES ('a'), ('b'), ('c');");

        var full = Helpers.BackupPath("dns");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "dns_" + Guid.NewGuid().ToString("N")[..8];
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

            // Existing rows preserved.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT array_agg(id ORDER BY id) FROM dns_t";
                var ids = (long[])(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.Equal(new long[] { 1000, 1001, 1002 }, ids);
            }

            // New INSERTs continue the sequence past last_value, no
            // reuse of 1000/1001/1002.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "INSERT INTO dns_t(v) VALUES ('d') RETURNING id";
                var newId = (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.True(newId >= 1003,
                    $"Sequence must continue forward, got {newId}");
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
