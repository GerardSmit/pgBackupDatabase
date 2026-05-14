using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Deeper RLS coverage: FORCE RLS, RESTRICTIVE policies, multi-policy
/// stacks, and USING vs WITH CHECK distinct clauses. The matrix smoke
/// test only verifies a single PERMISSIVE SELECT policy + ENABLE.
/// </summary>
public sealed class RowLevelSecurityAdvancedTests
{
    private readonly PgContainerFixture _pg;

    public RowLevelSecurityAdvancedTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Force_Rls_And_Multi_Policy_RoundTrip()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await using (var admin = await _pg.AdminAsync())
        {
            await admin.ExecAsync(
                "DO $$ BEGIN " +
                "IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='rls_alice') " +
                "THEN CREATE ROLE rls_alice; END IF; " +
                "IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='rls_bob') " +
                "THEN CREATE ROLE rls_bob; END IF; " +
                "END $$;");
        }

        await src.ExecAsync(
            "CREATE TABLE rls_doc(id int PRIMARY KEY, owner_name text NOT NULL, " +
            "  secret text NOT NULL, vis int NOT NULL);" +
            "ALTER TABLE rls_doc ENABLE ROW LEVEL SECURITY;" +
            "ALTER TABLE rls_doc FORCE ROW LEVEL SECURITY;" +
            "CREATE POLICY rls_doc_select_public " +
            "  ON rls_doc FOR SELECT TO PUBLIC USING (vis = 1);" +
            "CREATE POLICY rls_doc_select_alice " +
            "  ON rls_doc FOR SELECT TO rls_alice USING (owner_name = 'alice');" +
            "CREATE POLICY rls_doc_insert_alice " +
            "  ON rls_doc FOR INSERT TO rls_alice " +
            "  WITH CHECK (owner_name = 'alice' AND vis IN (0,1));" +
            "CREATE POLICY rls_doc_update_alice " +
            "  ON rls_doc FOR UPDATE TO rls_alice " +
            "  USING (owner_name = 'alice') " +
            "  WITH CHECK (owner_name = 'alice' AND vis IN (0,1));" +
            "CREATE POLICY rls_doc_restrict_no_secrets " +
            "  ON rls_doc AS RESTRICTIVE FOR ALL TO PUBLIC " +
            "  USING (secret NOT LIKE 'top_%');" +
            "INSERT INTO rls_doc VALUES (1,'alice','ok',1),(2,'alice','top_x',0)," +
            "  (3,'bob','ok',1),(4,'bob','top_y',0);");

        var full = Helpers.BackupPath("rls_adv");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "rls_adv_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT c.relrowsecurity, c.relforcerowsecurity, " +
                    "       (SELECT count(*) FROM pg_policy WHERE polrelid = c.oid), " +
                    "       (SELECT count(*) FROM pg_policy " +
                    "        WHERE polrelid = c.oid AND polpermissive = false) " +
                    "FROM pg_class c WHERE c.oid = 'rls_doc'::regclass";
                await using var rdr = await cmd.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
                Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
                Assert.True(rdr.GetBoolean(0));
                Assert.True(rdr.GetBoolean(1));
                Assert.Equal(5L, rdr.GetInt64(2));
                Assert.Equal(1L, rdr.GetInt64(3));
            }

            // Bytewise check the policy definitions survive — important that
            // USING vs WITH CHECK survive separately.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT polname, polcmd, " +
                    "       pg_get_expr(polqual, polrelid), " +
                    "       pg_get_expr(polwithcheck, polrelid) " +
                    "FROM pg_policy WHERE polrelid = 'rls_doc'::regclass " +
                    "ORDER BY polname";
                await using var rdr = await cmd.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
                var rows = new List<(string name, char cmd, string? q, string? wc)>();
                while (await rdr.ReadAsync(TestContext.Current.CancellationToken))
                {
                    rows.Add((rdr.GetString(0), rdr.GetChar(1),
                        rdr.IsDBNull(2) ? null : rdr.GetString(2),
                        rdr.IsDBNull(3) ? null : rdr.GetString(3)));
                }
                Assert.Equal(5, rows.Count);

                var ins = rows.Single(x => x.name == "rls_doc_insert_alice");
                Assert.Null(ins.q);
                Assert.NotNull(ins.wc);
                Assert.Contains("owner_name", ins.wc);

                var upd = rows.Single(x => x.name == "rls_doc_update_alice");
                Assert.NotNull(upd.q);
                Assert.NotNull(upd.wc);
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
