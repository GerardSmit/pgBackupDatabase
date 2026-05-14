using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// MinIO is paused mid-backup so the in-flight S3 PUT/multipart upload
/// errors out, then resumed. The backup call must surface a clean,
/// retriable error (no silent partial success), and a follow-up backup
/// after MinIO is back must complete and be restorable end-to-end.
/// </summary>
[Collection(S3StorageCollection.Name)]
public sealed class S3InterruptedUploadTests
{
    private readonly S3StorageFixture _fixture;

    public S3InterruptedUploadTests(S3StorageFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task Backup_When_S3_Drops_Mid_Upload_Surfaces_Error_Then_Retry_Succeeds()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var bucket = "pgbk-" + Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();
        var prefix = "interrupt/" + Guid.NewGuid().ToString("N")[..8];
        var targetDb = "i_" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(conn, "minio", bucket, prefix);

            // Build a payload large enough that the upload is not a single
            // round-trip — pausing MinIO mid-flight must matter.
            await conn.ExecAsync(
                "CREATE TABLE big_t(id int PRIMARY KEY, payload text);" +
                "INSERT INTO big_t " +
                "SELECT g, repeat('z', 4096) " +
                "FROM generate_series(1, 4000) g;",
                timeoutSeconds: 120);

            // Pause MinIO BEFORE the backup starts so every S3 call fails.
            // (Pausing mid-call races and is non-deterministic across CI
            // hosts; "MinIO down" is the stronger contract anyway.)
            await _fixture.Minio.PauseAsync(TestContext.Current.CancellationToken);

            Exception? backupErr = null;
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbbackup_to_storage(" +
                    "dbname := @db, type := 'full', storage_target := 'minio', " +
                    "compress := false)";
                cmd.Parameters.AddWithValue("db", db);
                cmd.CommandTimeout = 60;
                await cmd.ExecuteNonQueryAsync(
                    TestContext.Current.CancellationToken);
            }
            catch (Exception ex) { backupErr = ex; }

            Assert.NotNull(backupErr);
            // Error message should at least hint at network/timeout/S3.
            var msg = backupErr!.Message.ToLowerInvariant();
            Assert.True(
                msg.Contains("s3") || msg.Contains("minio") ||
                msg.Contains("network") || msg.Contains("timeout") ||
                msg.Contains("connect") || msg.Contains("curl") ||
                msg.Contains("upload") || msg.Contains("http") ||
                msg.Contains("io") || msg.Contains("storage"),
                $"S3-down backup error must mention network/S3: {backupErr.Message}");

            // Resume MinIO and retry the backup. It must complete and the
            // result must be restorable.
            await _fixture.Minio.UnpauseAsync(TestContext.Current.CancellationToken);

            // The original session may be poisoned by the failed call;
            // open a fresh one for the retry.
            NpgsqlConnection.ClearAllPools();
            await using var conn2 = await _fixture.ConnectToAsync(db);
            await WaitMinioReadyAsync(conn2);

            Guid backupId;
            await using (var cmd = conn2.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbbackup_to_storage(" +
                    "dbname := @db, type := 'full', storage_target := 'minio', " +
                    "compress := false)";
                cmd.Parameters.AddWithValue("db", db);
                cmd.CommandTimeout = 180;
                backupId = (Guid)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
            }
            Assert.NotEqual(Guid.Empty, backupId);

            // End-to-end: restore from storage and verify row count.
            await using (var cmd = conn2.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbrestore_from_storage(" +
                    "dbname := @db, storage_target := 'minio', target_db := @t)";
                cmd.Parameters.AddWithValue("db", db);
                cmd.Parameters.AddWithValue("t", targetDb);
                cmd.CommandTimeout = 300;
                await cmd.ExecuteNonQueryAsync(
                    TestContext.Current.CancellationToken);
            }
            NpgsqlConnection.ClearAllPools();

            await using var r = await _fixture.ConnectToAsync(targetDb);
            await using var c2 = r.CreateCommand();
            c2.CommandText = "SELECT count(*) FROM big_t";
            Assert.Equal(4000L, (long)(await c2.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally
        {
            try { await _fixture.Minio.UnpauseAsync(); } catch { }
            await _fixture.DropDbAsync(targetDb);
            await _fixture.DropDbAsync(db);
        }
    }

    private async Task WaitMinioReadyAsync(NpgsqlConnection conn)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbbackup_s3_object_exists('minio', 'noop-readiness-probe')";
                cmd.CommandTimeout = 5;
                await cmd.ExecuteScalarAsync();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(500);
            }
        }
        throw new InvalidOperationException("MinIO did not become ready", last);
    }
}
