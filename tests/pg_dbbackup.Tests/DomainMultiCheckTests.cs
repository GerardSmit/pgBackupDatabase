using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// DOMAIN with multiple named CHECK constraints + DEFAULT + NOT NULL.
/// All clauses must survive restore so the column constraints behave
/// identically.
/// </summary>
public sealed class DomainMultiCheckTests
{
    private readonly PgContainerFixture _pg;

    public DomainMultiCheckTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Domain_With_Multiple_Checks_Default_NotNull_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE DOMAIN positive_pct AS int " +
            "  DEFAULT 50 " +
            "  NOT NULL " +
            "  CONSTRAINT positive_pct_lo CHECK (VALUE >= 0) " +
            "  CONSTRAINT positive_pct_hi CHECK (VALUE <= 100);" +
            "CREATE TABLE dmc_t(id int PRIMARY KEY, pct positive_pct);" +
            "INSERT INTO dmc_t(id) VALUES (1);" +
            "INSERT INTO dmc_t(id, pct) VALUES (2, 75);");

        var full = Helpers.BackupPath("dmc");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "dmc_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            // Both named CHECK constraints survive. NOT NULL is also
            // emitted as its own pg_constraint row by PG 16+; allow it.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT array_agg(conname ORDER BY conname) " +
                    "FROM pg_constraint c " +
                    "JOIN pg_type t ON t.oid = c.contypid " +
                    "WHERE t.typname = 'positive_pct' AND c.contype = 'c'";
                var arr = (string[])(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.Equal(
                    new[] { "positive_pct_hi", "positive_pct_lo" }, arr);
            }

            // DEFAULT used on new INSERT.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText = "SELECT pct FROM dmc_t WHERE id = 1";
                Assert.Equal(50, (int)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // Both CHECK constraints enforced.
            foreach (var bad in new[] { -1, 101 })
            {
                await using var cmd = r.CreateCommand();
                cmd.CommandText = $"INSERT INTO dmc_t VALUES (99, {bad})";
                var ex = await Assert.ThrowsAsync<PostgresException>(
                    () => cmd.ExecuteNonQueryAsync(
                        TestContext.Current.CancellationToken));
                Assert.Equal("23514", ex.SqlState);
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
