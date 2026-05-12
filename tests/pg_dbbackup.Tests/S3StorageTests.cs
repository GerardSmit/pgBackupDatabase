using Npgsql;
using NpgsqlTypes;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

[Collection(S3StorageCollection.Name)]
public sealed class S3StorageTests
{
    private readonly S3StorageFixture _fixture;

    public S3StorageTests(S3StorageFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SimpleFullBackupUploadsManifestAndRestoresFromMinio()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var bucket = BucketName();
        var prefix = Prefix();
        var targetDb = "restore_" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(conn, "minio", bucket, prefix);
            await conn.ExecAsync(
                "CREATE TABLE items(id int PRIMARY KEY, name text);" +
                "INSERT INTO items VALUES (1, 'alpha'), (2, 'beta');");

            var backupId = await BackupToStorageAsync(conn, "full");

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*), bool_and(dbbackup.pg_dbbackup_s3_object_exists('minio', object_key)), " +
                    "       bool_and(dbbackup.pg_dbbackup_s3_object_exists('minio', manifest_key)) " +
                    "FROM dbbackup.backup_artifacts WHERE backup_id = @id";
                cmd.Parameters.AddWithValue("id", backupId);
                await using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.True(reader.GetBoolean(1));
                Assert.True(reader.GetBoolean(2));
            }

            await RestoreFromStorageAsync(conn, db, targetDb);

            await using var restored = await _fixture.ConnectToAsync(targetDb);
            await using var countCmd = restored.CreateCommand();
            countCmd.CommandText = "SELECT count(*), string_agg(name, ',' ORDER BY id) FROM items";
            await using var countReader = await countCmd.ExecuteReaderAsync();
            Assert.True(await countReader.ReadAsync());
            Assert.Equal(2L, countReader.GetInt64(0));
            Assert.Equal("alpha,beta", countReader.GetString(1));
        }
        finally
        {
            await _fixture.DropDbAsync(targetDb);
            await _fixture.DropDbAsync(db);
        }
    }

    [Fact]
    public async Task FreshCatalogSearchesAndRestoresFromS3Manifests()
    {
        await using var source = await _fixture.CreateFreshDbWithExtensionAsync();
        var sourceDb = source.Database!;
        var bucket = BucketName();
        var prefix = Prefix();
        var targetDb = "restore_" + Guid.NewGuid().ToString("N")[..8];
        string? catalogDb = null;

        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(source, "minio", bucket, prefix);
            await source.ExecAsync(
                "CREATE TABLE catalog_items(id int PRIMARY KEY, value text);" +
                "INSERT INTO catalog_items VALUES (1, 'from-s3');");

            await BackupToStorageAsync(source, "full");

            await using var catalog = await _fixture.CreateFreshDbWithExtensionAsync();
            catalogDb = catalog.Database!;
            await ConfigureTargetOnlyAsync(catalog, "minio", bucket, prefix);

            await using (var search = catalog.CreateCommand())
            {
                search.CommandText =
                    "SELECT restorable, array_length(required_artifacts, 1) " +
                    "FROM dbbackup.search_backups(dbname := @db, storage_target := 'minio')";
                search.Parameters.AddWithValue("db", sourceDb);
                await using var reader = await search.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.True(reader.GetBoolean(0));
                Assert.Equal(1, reader.GetInt32(1));
            }

            await catalog.ExecAsync("DELETE FROM dbbackup.backup_artifacts");

            await using (var search = catalog.CreateCommand())
            {
                search.CommandText =
                    "SELECT count(*) FROM dbbackup.pg_dbbackup_search(@db, 'minio')";
                search.Parameters.AddWithValue("db", sourceDb);
                Assert.Equal(1L, (long)(await search.ExecuteScalarAsync())!);
            }

            await catalog.ExecAsync("DELETE FROM dbbackup.backup_artifacts");
            await RestoreFromStorageAsync(catalog, sourceDb, targetDb);

            await using (var count = catalog.CreateCommand())
            {
                count.CommandText =
                    "SELECT count(*) FROM dbbackup.backup_artifacts WHERE dbname = @db";
                count.Parameters.AddWithValue("db", sourceDb);
                Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
            }

            await using var restored = await _fixture.ConnectToAsync(targetDb);
            await using var check = restored.CreateCommand();
            check.CommandText = "SELECT value FROM catalog_items WHERE id = 1";
            Assert.Equal("from-s3", (string)(await check.ExecuteScalarAsync())!);
        }
        finally
        {
            await _fixture.DropDbAsync(targetDb);
            if (catalogDb is not null)
                await _fixture.DropDbAsync(catalogDb);
            await _fixture.DropDbAsync(sourceDb);
        }
    }

    [Fact]
    public async Task FullModeLogChainUploadsAndRestoresLatestFromMinio()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var bucket = BucketName();
        var prefix = Prefix();
        var targetDb = "restore_" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(conn, "minio", bucket, prefix);
            await conn.SetModeFullAsync();
            await conn.ExecAsync(
                "CREATE TABLE ledger(id int PRIMARY KEY, note text);" +
                "INSERT INTO ledger VALUES (1, 'full');");

            var fullId = await BackupToStorageAsync(conn, "full");

            await conn.ExecAsync("INSERT INTO ledger VALUES (2, 'log')");
            var logId = await BackupToStorageAsync(conn, "log");

            await using (var search = conn.CreateCommand())
            {
                search.CommandText =
                    "SELECT backup_type FROM dbbackup.pg_dbbackup_search(@db, 'minio') " +
                    "ORDER BY range_end_time, backup_id";
                search.Parameters.AddWithValue("db", db);
                var seen = new List<string>();
                await using var reader = await search.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    seen.Add(reader.GetString(0));
                Assert.Contains("full", seen);
                Assert.Contains("log", seen);
            }

            Assert.NotEqual(fullId, logId);

            await RestoreFromStorageAsync(conn, db, targetDb);
            await using var restored = await _fixture.ConnectToAsync(targetDb);
            await using var countCmd = restored.CreateCommand();
            countCmd.CommandText = "SELECT count(*), string_agg(note, ',' ORDER BY id) FROM ledger";
            await using var countReader = await countCmd.ExecuteReaderAsync();
            Assert.True(await countReader.ReadAsync());
            Assert.Equal(2L, countReader.GetInt64(0));
            Assert.Equal("full,log", countReader.GetString(1));
        }
        finally
        {
            await _fixture.DropDbAsync(targetDb);
            await _fixture.DropDbAsync(db);
        }
    }

    [Fact]
    public async Task SimpleDifferentialChainUploadsAndRestoresFromMinio()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var bucket = BucketName();
        var prefix = Prefix();
        var targetDb = "restore_" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(conn, "minio", bucket, prefix);
            await conn.ExecAsync(
                "CREATE TABLE items(id int PRIMARY KEY, name text);" +
                "INSERT INTO items VALUES (1, 'full');");

            var fullId = await BackupToStorageAsync(conn, "full");

            await conn.ExecAsync("INSERT INTO items VALUES (2, 'diff');");
            var diffId = await BackupToStorageAsync(conn, "differential");

            Assert.NotEqual(fullId, diffId);
            await RestoreFromStorageAsync(conn, db, targetDb);

            await using var restored = await _fixture.ConnectToAsync(targetDb);
            await using var countCmd = restored.CreateCommand();
            countCmd.CommandText = "SELECT count(*), string_agg(name, ',' ORDER BY id) FROM items";
            await using var countReader = await countCmd.ExecuteReaderAsync();
            Assert.True(await countReader.ReadAsync());
            Assert.Equal(2L, countReader.GetInt64(0));
            Assert.Equal("full,diff", countReader.GetString(1));
        }
        finally
        {
            await _fixture.DropDbAsync(targetDb);
            await _fixture.DropDbAsync(db);
        }
    }

    [Fact]
    public async Task SearchBackupsTreatsDeletedRemoteObjectAsGap()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var bucket = BucketName();
        var prefix = Prefix();

        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(conn, "minio", bucket, prefix);
            await conn.ExecAsync(
                "CREATE TABLE items(id int PRIMARY KEY);" +
                "INSERT INTO items VALUES (1);");

            var backupId = await BackupToStorageAsync(conn, "full");

            string objectKey;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT object_key FROM dbbackup.backup_artifacts WHERE backup_id = @id";
                cmd.Parameters.AddWithValue("id", backupId);
                objectKey = (string)(await cmd.ExecuteScalarAsync())!;
            }

            await using (var delete = conn.CreateCommand())
            {
                delete.CommandText =
                    "SELECT dbbackup.pg_dbbackup_s3_delete_object('minio', @key)";
                delete.Parameters.AddWithValue("key", objectKey);
                await delete.ExecuteNonQueryAsync();
            }

            await using (var search = conn.CreateCommand())
            {
                search.CommandText =
                    "SELECT restorable, gap_reason " +
                    "FROM dbbackup.search_backups(dbname := @db, storage_target := 'minio')";
                search.Parameters.AddWithValue("db", db);
                await using var reader = await search.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.False(reader.GetBoolean(0));
                Assert.Equal("one or more remote artifacts missing", reader.GetString(1));
            }
        }
        finally
        {
            await _fixture.DropDbAsync(db);
        }
    }

    [Fact]
    public async Task StorageBackupAsyncReturnsJobIdAndUploadsInBackground()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var bucket = BucketName();
        var prefix = Prefix();

        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(conn, "minio", bucket, prefix);
            await conn.ExecAsync(
                "CREATE TABLE async_items(id int PRIMARY KEY, value text);" +
                "INSERT INTO async_items VALUES (1, 'queued');");

            Guid jobId;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbbackup_to_storage_async(" +
                    "dbname := @db, type := 'full', storage_target := 'minio', " +
                    "compress := false)";
                cmd.Parameters.AddWithValue("db", db);
                jobId = (Guid)(await cmd.ExecuteScalarAsync())!;
            }

            await using (var wait = conn.CreateCommand())
            {
                wait.CommandText = "SELECT dbbackup.pg_dbbackup_wait(@id, 120)";
                wait.Parameters.AddWithValue("id", jobId);
                Assert.Equal("completed", (string)(await wait.ExecuteScalarAsync())!);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT status, progress_pct FROM dbbackup.pg_dbbackup_status(@id)";
                cmd.Parameters.AddWithValue("id", jobId);
                await using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("completed", reader.GetString(0));
                Assert.Equal(100, reader.GetInt32(1));
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM dbbackup.backup_artifacts WHERE dbname = @db";
                cmd.Parameters.AddWithValue("db", db);
                Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
            }
        }
        finally
        {
            await _fixture.DropDbAsync(db);
        }
    }

    [Fact]
    public async Task LargeBackupUsesCatalogTrackedMultipartUpload()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var bucket = BucketName();
        var prefix = Prefix();
        var targetDb = "restore_" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(conn, "minio", bucket, prefix);
            await conn.ExecAsync(
                "CREATE TABLE large_items(id int PRIMARY KEY, payload text);" +
                "INSERT INTO large_items " +
                "SELECT g, repeat(md5(g::text), 12000) " +
                "FROM generate_series(1, 35) g;",
                timeoutSeconds: 180);

            var backupId = await BackupToStorageAsync(conn, "full");

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT a.size_bytes, count(u.*), bool_and(u.status = 'completed') " +
                    "FROM dbbackup.backup_artifacts a " +
                    "LEFT JOIN dbbackup.storage_uploads u ON u.backup_id = a.backup_id " +
                    "WHERE a.backup_id = @id " +
                    "GROUP BY a.size_bytes";
                cmd.Parameters.AddWithValue("id", backupId);
                await using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.True(reader.GetInt64(0) > 8L * 1024 * 1024,
                    "test backup must cross the multipart threshold");
                Assert.Equal(1L, reader.GetInt64(1));
                Assert.True(reader.GetBoolean(2));
            }

            await RestoreFromStorageAsync(conn, db, targetDb);

            await using var restored = await _fixture.ConnectToAsync(targetDb);
            await using var check = restored.CreateCommand();
            check.CommandText =
                "SELECT count(*), sum(length(payload)) FROM large_items";
            await using var checkReader = await check.ExecuteReaderAsync();
            Assert.True(await checkReader.ReadAsync());
            Assert.Equal(35L, checkReader.GetInt64(0));
            Assert.Equal(35L * 32L * 12000L, checkReader.GetInt64(1));
        }
        finally
        {
            await _fixture.DropDbAsync(targetDb);
            await _fixture.DropDbAsync(db);
        }
    }

    [Fact]
    public async Task BackupSetsSchedulesAndRetentionCatalogAreMutable()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var bucket = BucketName();
        var prefix = Prefix();

        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(conn, "minio", bucket, prefix);

            await conn.ExecAsync(
                "SELECT dbbackup.create_backup_set('prod_core', 'minio');" +
                $"SELECT dbbackup.add_database_to_backup_set('prod_core', '{db}');");

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.create_schedule(" +
                    "backup_set := 'prod_core', name := 'log-10m', " +
                    "backup_type := 'log', every := interval '10 minutes')";
                var id = (Guid)(await cmd.ExecuteScalarAsync())!;
                Assert.NotEqual(Guid.Empty, id);
            }

            await conn.ExecAsync(
                "SELECT dbbackup.alter_schedule('prod_core', 'log-10m', every := interval '5 minutes');" +
                "SELECT dbbackup.pause_schedule('prod_core', 'log-10m');" +
                "SELECT dbbackup.resume_schedule('prod_core', 'log-10m');");

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT every = interval '5 minutes', enabled " +
                    "FROM dbbackup.backup_schedules " +
                    "WHERE backup_set = 'prod_core' AND name = 'log-10m'";
                await using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.True(reader.GetBoolean(0));
                Assert.True(reader.GetBoolean(1));
            }

            await conn.ExecAsync($"SELECT dbbackup.set_backup_set_databases('prod_core', ARRAY['{db}']);");

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM dbbackup.list_backup_set_databases('prod_core') " +
                    "WHERE dbname = @db AND active";
                cmd.Parameters.AddWithValue("db", db);
                Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
            }

            await conn.ExecAsync("SELECT dbbackup.drop_schedule('prod_core', 'log-10m')");
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM dbbackup.backup_schedules";
                Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
            }
        }
        finally
        {
            await _fixture.DropDbAsync(db);
        }
    }

    [Fact]
    public async Task DueIntervalScheduleRunsBackupSetMembersToMinio()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var bucket = BucketName();
        var prefix = Prefix();

        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(conn, "minio", bucket, prefix);
            await conn.ExecAsync(
                "CREATE TABLE scheduled_items(id int PRIMARY KEY, value text);" +
                "INSERT INTO scheduled_items VALUES (1, 'first');" +
                "SELECT dbbackup.create_backup_set('prod_core', 'minio');" +
                $"SELECT dbbackup.add_database_to_backup_set('prod_core', '{db}');" +
                "SELECT dbbackup.create_schedule(" +
                "  backup_set := 'prod_core', name := 'full-fast', " +
                "  backup_type := 'full', every := interval '1 second');");

            await using (var run = conn.CreateCommand())
            {
                run.CommandText = "SELECT dbbackup.pg_dbbackup_run_due_schedules(now())";
                run.CommandTimeout = 180;
                Assert.Equal(1, (int)(await run.ExecuteScalarAsync())!);
            }

            await using (var count = conn.CreateCommand())
            {
                count.CommandText =
                    "SELECT count(*) FROM dbbackup.backup_artifacts " +
                    "WHERE backup_set = 'prod_core' AND dbname = @db";
                count.Parameters.AddWithValue("db", db);
                Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
            }

            await using (var runAgain = conn.CreateCommand())
            {
                runAgain.CommandText = "SELECT dbbackup.pg_dbbackup_run_due_schedules(now())";
                runAgain.CommandTimeout = 180;
                Assert.Equal(0, (int)(await runAgain.ExecuteScalarAsync())!);
            }

            await conn.ExecAsync(
                "UPDATE dbbackup.backup_schedules " +
                "SET last_run_at = now() - interval '2 seconds' " +
                "WHERE backup_set = 'prod_core' AND name = 'full-fast';" +
                "INSERT INTO scheduled_items VALUES (2, 'second');");

            await using (var runDue = conn.CreateCommand())
            {
                runDue.CommandText = "SELECT dbbackup.pg_dbbackup_run_due_schedules(now())";
                runDue.CommandTimeout = 180;
                Assert.Equal(1, (int)(await runDue.ExecuteScalarAsync())!);
            }

            await using (var count = conn.CreateCommand())
            {
                count.CommandText =
                    "SELECT count(*), min(run_count) " +
                    "FROM dbbackup.backup_artifacts a " +
                    "CROSS JOIN dbbackup.backup_schedules s " +
                    "WHERE a.backup_set = 'prod_core' AND a.dbname = @db " +
                    "  AND s.backup_set = 'prod_core' AND s.name = 'full-fast'";
                count.Parameters.AddWithValue("db", db);
                await using var reader = await count.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(2L, reader.GetInt64(0));
                Assert.Equal(2L, reader.GetInt64(1));
            }
        }
        finally
        {
            await _fixture.DropDbAsync(db);
        }
    }

    [Fact]
    public async Task FailedFullStorageUploadDoesNotActivateChainOrArtifact()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;

        try
        {
            await conn.ExecAsync(
                "SELECT dbbackup.create_s3_target(" +
                "name := 'bad_minio', bucket := 'missing-bucket', " +
                "region := 'us-east-1', endpoint_url := 'http://minio:1', " +
                "force_path_style := true);" +
                "CREATE TABLE guarded(id int PRIMARY KEY);" +
                "INSERT INTO guarded VALUES (1);");
            await conn.SetModeFullAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbbackup_to_storage(" +
                    "dbname := @db, type := 'full', storage_target := 'bad_minio', " +
                    "compress := false)";
                cmd.Parameters.AddWithValue("db", db);
                cmd.CommandTimeout = 120;
                await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteScalarAsync());
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM dbbackup.backup_artifacts";
                Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM dbbackup.logical_chains";
                Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
            }
        }
        finally
        {
            await _fixture.DropDbAsync(db);
        }
    }

    private static async Task<Guid> BackupToStorageAsync(NpgsqlConnection conn, string type)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbbackup_to_storage(" +
            "dbname := @db, type := @type, storage_target := 'minio', compress := false)";
        cmd.Parameters.AddWithValue("db", conn.Database!);
        cmd.Parameters.AddWithValue("type", type);
        cmd.CommandTimeout = 120;
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task RestoreFromStorageAsync(
        NpgsqlConnection conn,
        string db,
        string targetDb)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbrestore_from_storage(" +
            "dbname := @db, storage_target := 'minio', target_db := @target)";
        cmd.Parameters.AddWithValue("db", db);
        cmd.Parameters.AddWithValue("target", targetDb);
        cmd.CommandTimeout = 120;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ConfigureTargetOnlyAsync(
        NpgsqlConnection conn,
        string targetName,
        string bucket,
        string prefix)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.create_s3_target(" +
            "name := @name, bucket := @bucket, prefix := @prefix, " +
            "region := 'us-east-1', endpoint_url := 'http://minio:9000', " +
            "force_path_style := true)";
        cmd.Parameters.AddWithValue("name", targetName);
        cmd.Parameters.AddWithValue("bucket", bucket);
        cmd.Parameters.AddWithValue("prefix", prefix);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string BucketName() =>
        "pgdbbackup-" + Guid.NewGuid().ToString("N")[..16].ToLowerInvariant();

    private static string Prefix() =>
        "tests/" + Guid.NewGuid().ToString("N")[..16].ToLowerInvariant();
}
