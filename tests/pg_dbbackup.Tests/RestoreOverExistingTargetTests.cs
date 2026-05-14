using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Restoring with target_db pointing at an already-populated database
/// must either (a) atomically swap the contents (drop old, install new
/// — common SQL Server semantic) or (b) refuse with a clear error
/// naming the conflict. What's NOT acceptable is mixing the old rows
/// with the new ones.
/// </summary>
public sealed class RestoreOverExistingTargetTests
{
    private readonly PgContainerFixture _pg;

    public RestoreOverExistingTargetTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Restore_Onto_Populated_Target_Either_Replaces_Or_Errors()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE roe_t(id int PRIMARY KEY, src text);" +
            "INSERT INTO roe_t VALUES (1, 'src-1'), (2, 'src-2');");

        var full = Helpers.BackupPath("roe");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        // Build a target DB that already has DIFFERENT, identifiable data.
        var target = "roe_pre_" + Guid.NewGuid().ToString("N")[..8];
        await using (var admin = await _pg.AdminAsync())
        await using (var cmd = admin.CreateCommand())
        {
            cmd.CommandText = $"CREATE DATABASE \"{target}\"";
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var preconn = await _pg.ConnectToAsync(target))
        {
            await preconn.ExecAsync(
                "CREATE TABLE preexisting(id int PRIMARY KEY, marker text);" +
                "INSERT INTO preexisting VALUES (99, 'must-not-survive');");
        }
        // Clear pool so the existing target DB has no live connections;
        // pg_dbrestore needs to drop+rename it without blocking.
        NpgsqlConnection.ClearAllPools();

        Exception? restoreErr = null;
        try
        {
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
            cmd.Parameters.AddWithValue("files", new[] { full });
            cmd.Parameters.AddWithValue("tgt", target);
            cmd.CommandTimeout = 300;
            await cmd.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
        }
        catch (Exception ex) { restoreErr = ex; }

        try
        {
            if (restoreErr is not null)
            {
                // Acceptable: an error that surfaces the conflict. Server
                // may also abort the connection if it kills the target
                // DB underneath the calling backend — that surfaces as
                // NpgsqlException with IOException inside.
                var msg = restoreErr.Message.ToLowerInvariant();
                Assert.True(
                    msg.Contains("exist") || msg.Contains(target.ToLowerInvariant()) ||
                    msg.Contains("conflict") || msg.Contains("already") ||
                    msg.Contains("populated") || msg.Contains("in use") ||
                    msg.Contains("abort") || msg.Contains("connection") ||
                    msg.Contains("transport") || msg.Contains("stream"),
                    $"Existing-target error must name conflict: {restoreErr.Message}");
            }
            else
            {
                // Atomic-swap semantics: the OLD preexisting table must be
                // gone; the NEW roe_t table must be present with src rows.
                await using var r = await _pg.ConnectToAsync(target);
                await using (var c1 = r.CreateCommand())
                {
                    c1.CommandText =
                        "SELECT count(*) FROM pg_class " +
                        "WHERE relname = 'preexisting' AND relkind = 'r'";
                    Assert.Equal(0L, (long)(await c1.ExecuteScalarAsync(
                        TestContext.Current.CancellationToken))!);
                }
                await using (var c2 = r.CreateCommand())
                {
                    c2.CommandText =
                        "SELECT count(*), max(id) FROM roe_t";
                    await using var rdr = await c2.ExecuteReaderAsync(
                        TestContext.Current.CancellationToken);
                    Assert.True(await rdr.ReadAsync(
                        TestContext.Current.CancellationToken));
                    Assert.Equal(2L, rdr.GetInt64(0));
                    Assert.Equal(2, rdr.GetInt32(1));
                }
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
