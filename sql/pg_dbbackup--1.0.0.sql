\echo Use "CREATE EXTENSION pg_dbbackup" to load this file. \quit

-- Mode configuration table
CREATE TABLE dbbackup.db_config (
    db_oid    oid PRIMARY KEY,
    db_name   text NOT NULL,
    mode      text NOT NULL DEFAULT 'simple'
        CHECK (mode IN ('simple', 'full'))
);

-- FULL-mode logical PITR chain metadata. DML is not persisted here; it is
-- decoded from PostgreSQL WAL through the chain's logical replication slot
-- and written directly to LOG .bak files. Chain slots are created with
-- failover=true so PostgreSQL 17+ slot synchronization can carry them to
-- standbys for primary promotion.
CREATE TABLE dbbackup.logical_chains (
    db_oid        oid PRIMARY KEY,
    db_name       text NOT NULL,
    slot_name     name NOT NULL UNIQUE,
    confirmed_lsn pg_lsn NOT NULL,
    updated_at    timestamptz NOT NULL DEFAULT clock_timestamp()
);

-- Inspect whether a FULL-mode chain slot is safe to use after standby
-- promotion. On the primary, failover=true is the expected chain-slot state.
-- On a standby, PostgreSQL must also report synced=true, temporary=false, and
-- no invalidation reason before the slot survives promotion.
CREATE FUNCTION dbbackup.pg_dbbackup_failover_slot_status(dbname text)
RETURNS TABLE (
    slot_name           name,
    slot_exists         boolean,
    database_name       text,
    slot_type           text,
    plugin              name,
    failover            boolean,
    synced              boolean,
    temporary           boolean,
    invalidation_reason text,
    restart_lsn         pg_lsn,
    confirmed_flush_lsn pg_lsn,
    catalog_xmin        xid,
    wal_status          text,
    standby_ready       boolean
)
LANGUAGE sql
STABLE
STRICT
AS $$
    SELECT
        ('_pg_dbbackup_' || d.oid::text)::name AS slot_name,
        s.slot_name IS NOT NULL AS slot_exists,
        d.datname AS database_name,
        s.slot_type,
        s.plugin,
        COALESCE(s.failover, false) AS failover,
        COALESCE(s.synced, false) AS synced,
        COALESCE(s.temporary, false) AS temporary,
        s.invalidation_reason,
        s.restart_lsn,
        s.confirmed_flush_lsn,
        s.catalog_xmin,
        s.wal_status,
        COALESCE(
            s.slot_type = 'logical'
            AND s.plugin = 'pg_dbbackup'
            AND s.failover
            AND s.synced
            AND NOT s.temporary
            AND s.invalidation_reason IS NULL,
            false) AS standby_ready
    FROM pg_database d
    LEFT JOIN pg_replication_slots s
      ON s.slot_name = ('_pg_dbbackup_' || d.oid::text)::name
    WHERE d.datname = $1
$$;

CREATE FUNCTION dbbackup.pg_dbbackup_failover_slot_ready(dbname text)
RETURNS boolean
LANGUAGE sql
STABLE
STRICT
AS $$
    SELECT COALESCE((
        SELECT standby_ready
        FROM dbbackup.pg_dbbackup_failover_slot_status($1)
    ), false)
$$;

CREATE FUNCTION dbbackup.pg_dbbackup_wait_failover_slot_ready(
    dbname       text,
    timeout_secs int DEFAULT 60
) RETURNS boolean
LANGUAGE plpgsql
VOLATILE
STRICT
AS $$
DECLARE
    deadline timestamptz;
BEGIN
    IF timeout_secs < 0 THEN
        RAISE EXCEPTION 'timeout_secs must be >= 0';
    END IF;

    deadline := clock_timestamp() + make_interval(secs => timeout_secs);

    LOOP
        IF dbbackup.pg_dbbackup_failover_slot_ready(dbname) THEN
            RETURN true;
        END IF;

        IF clock_timestamp() >= deadline THEN
            RETURN false;
        END IF;

        PERFORM pg_sleep(0.2);
    END LOOP;
END
$$;

-- Database-local DDL journal. Logical decoding does not emit DDL by itself,
-- so FULL-mode LOG backups decode this table specially and replay the captured
-- command text in transaction order with row changes.
CREATE TABLE dbbackup.ddl_log (
    id          bigserial PRIMARY KEY,
    txid        bigint NOT NULL DEFAULT txid_current(),
    lsn         pg_lsn NOT NULL DEFAULT pg_current_wal_lsn(),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    tag         text NOT NULL,
    command     text NOT NULL
);

-- Transaction-end state journals for objects PostgreSQL logical decoding does
-- not expose as ordinary user-table DML. The output plugin decodes these
-- tables specially and emits replay SQL instead of restoring the journal rows.
CREATE TABLE dbbackup.sequence_state_cache (
    schema_name   text NOT NULL,
    sequence_name text NOT NULL,
    last_value    numeric NOT NULL,
    is_called     boolean NOT NULL,
    PRIMARY KEY (schema_name, sequence_name)
);

CREATE TABLE dbbackup.sequence_log (
    id            bigserial PRIMARY KEY,
    txid          bigint NOT NULL DEFAULT txid_current(),
    recorded_at   timestamptz NOT NULL DEFAULT clock_timestamp(),
    schema_name   text NOT NULL,
    sequence_name text NOT NULL,
    last_value    numeric NOT NULL,
    is_called     boolean NOT NULL
);

CREATE TABLE dbbackup.large_object_state_cache (
    loid        oid PRIMARY KEY,
    object_hash text NOT NULL
);

CREATE TABLE dbbackup.large_object_log (
    id           bigserial PRIMARY KEY,
    txid         bigint NOT NULL DEFAULT txid_current(),
    recorded_at  timestamptz NOT NULL DEFAULT clock_timestamp(),
    loid         oid NOT NULL,
    action       text NOT NULL CHECK (action IN ('snapshot', 'unlink')),
    snapshot_sql text NOT NULL
);

CREATE FUNCTION dbbackup.pg_dbbackup_capture_ddl_command_end()
RETURNS event_trigger
LANGUAGE plpgsql
AS $$
DECLARE
    q text := current_query();
    obj record;
BEGIN
    IF current_setting('dbbackup.replaying', true) = 'on' THEN
        RETURN;
    END IF;

    IF q IS NULL OR btrim(q) = '' THEN
        RETURN;
    END IF;

    SELECT * INTO obj
      FROM pg_event_trigger_ddl_commands()
     LIMIT 1;

    IF NOT FOUND THEN
        RETURN;
    END IF;

    IF obj.schema_name LIKE 'pg\_%'
       OR obj.schema_name IN (
            'dbbackup',
            'information_schema',
            '_timescaledb_internal',
            '_timescaledb_catalog',
            '_timescaledb_config',
            '_timescaledb_cache',
            '_timescaledb_functions',
            'timescaledb_information',
            'timescaledb_experimental') THEN
        RETURN;
    END IF;

    IF obj.in_extension AND tg_tag <> 'CREATE EXTENSION' THEN
        RETURN;
    END IF;

    IF q ~* '^\s*(CREATE|ALTER|DROP)\s+EXTENSION\s+pg_dbbackup\b' THEN
        RETURN;
    END IF;

    INSERT INTO dbbackup.ddl_log(tag, command)
    VALUES (tg_tag, q);
END
$$;

CREATE FUNCTION dbbackup.pg_dbbackup_capture_sql_drop()
RETURNS event_trigger
LANGUAGE plpgsql
AS $$
DECLARE
    q text := current_query();
BEGIN
    IF current_setting('dbbackup.replaying', true) = 'on' THEN
        RETURN;
    END IF;

    IF q IS NULL OR btrim(q) = '' THEN
        RETURN;
    END IF;

    IF q ~* '^\s*DROP\s+EXTENSION\s+pg_dbbackup\b' THEN
        RETURN;
    END IF;

    INSERT INTO dbbackup.ddl_log(tag, command)
    VALUES (tg_tag, q);
END
$$;

CREATE EVENT TRIGGER pg_dbbackup_ddl_command_end
    ON ddl_command_end
    EXECUTE FUNCTION dbbackup.pg_dbbackup_capture_ddl_command_end();

CREATE EVENT TRIGGER pg_dbbackup_sql_drop
    ON sql_drop
    EXECUTE FUNCTION dbbackup.pg_dbbackup_capture_sql_drop();

-- Set recovery mode for a database
CREATE FUNCTION dbbackup.pg_dbbackup_set_mode(
    dbname text,
    mode   text
) RETURNS void
AS 'MODULE_PATHNAME', 'pg_dbbackup_set_mode'
LANGUAGE C VOLATILE STRICT;

-- Get recovery mode for a database
CREATE FUNCTION dbbackup.pg_dbbackup_get_mode(
    dbname text
) RETURNS text
AS 'MODULE_PATHNAME', 'pg_dbbackup_get_mode'
LANGUAGE C VOLATILE STRICT;

-- Backup a database to a .bak file
CREATE FUNCTION dbbackup.pg_dbbackup(
    dbname        text,
    filepath      text,
    type          text    DEFAULT 'full',
    compress      boolean DEFAULT true,
    password      text    DEFAULT NULL,
    base_filepath text    DEFAULT NULL
) RETURNS text
AS 'MODULE_PATHNAME', 'pg_dbbackup'
LANGUAGE C VOLATILE;

-- Restore a database from .bak files
CREATE FUNCTION dbbackup.pg_dbrestore(
    dbname    text,
    files     text[],
    target_db text DEFAULT NULL,
    stop_at   timestamptz DEFAULT NULL,
    password  text DEFAULT NULL
) RETURNS void
AS 'MODULE_PATHNAME', 'pg_dbrestore'
LANGUAGE C VOLATILE;

-- Inspect .bak file header (like SQL Server RESTORE HEADERONLY)
CREATE FUNCTION dbbackup.pg_dbbackup_header(
    filepath text,
    OUT backup_type text,
    OUT mode        text,
    OUT db_name     text,
    OUT db_oid      oid,
    OUT created_at  timestamptz,
    OUT start_lsn   pg_lsn,
    OUT stop_lsn    pg_lsn,
    OUT pg_version  int4,
    OUT compressed  boolean,
    OUT encrypted   boolean,
    OUT base_lsn    pg_lsn
)
AS 'MODULE_PATHNAME', 'pg_dbbackup_header'
LANGUAGE C VOLATILE STRICT;

-- List files in a .bak (like SQL Server RESTORE FILELISTONLY)
CREATE FUNCTION dbbackup.pg_dbbackup_filelist(
    filepath text,
    password text DEFAULT NULL
) RETURNS TABLE (
    file_path text,
    file_size int8,
    checksum  text
)
AS 'MODULE_PATHNAME', 'pg_dbbackup_filelist'
LANGUAGE C VOLATILE;

-- Verify .bak file integrity
CREATE FUNCTION dbbackup.pg_dbbackup_verify(
    filepath  text,
    OUT is_valid boolean,
    OUT detail   text
)
AS 'MODULE_PATHNAME', 'pg_dbbackup_verify'
LANGUAGE C VOLATILE STRICT;

-- Roundtrip a buffer through compression + encryption.
-- Returns the processed (compressed/encrypted) blob; the C function
-- asserts the inverse recovers the original bytes.
CREATE FUNCTION dbbackup.pg_dbbackup_test_crypto(
    input    bytea,
    compress boolean,
    password text DEFAULT NULL
) RETURNS bytea
AS 'MODULE_PATHNAME', 'pg_dbbackup_test_crypto'
LANGUAGE C VOLATILE;

-- Return SQL script reproducing database metadata (extensions, schemas,
-- grants, default ACLs, comments, db-level settings).
CREATE FUNCTION dbbackup.pg_dbbackup_test_metadata(dbname text) RETURNS text
AS 'MODULE_PATHNAME', 'pg_dbbackup_test_metadata'
LANGUAGE C VOLATILE STRICT;

-- Return SQL script reproducing user-defined DDL objects.
CREATE FUNCTION dbbackup.pg_dbbackup_test_ddl(dbname text) RETURNS text
AS 'MODULE_PATHNAME', 'pg_dbbackup_test_ddl'
LANGUAGE C VOLATILE STRICT;

-- Off-box storage targets and backup catalog.
CREATE TABLE dbbackup.storage_targets (
    name                 text PRIMARY KEY,
    target_type          text NOT NULL
                         CHECK (target_type IN ('local', 's3', 's3-compatible')),
    bucket               text,
    prefix               text NOT NULL DEFAULT '',
    region               text,
    endpoint_url         text,
    force_path_style     boolean NOT NULL DEFAULT false,
    encryption           text NOT NULL DEFAULT 'none'
                         CHECK (encryption IN ('none', 'sse-s3', 'sse-kms')),
    kms_key_id           text,
    object_lock_mode     text NOT NULL DEFAULT 'off'
                         CHECK (object_lock_mode IN ('off', 'governance', 'compliance')),
    object_lock_retain_until text,
    max_retries          int NOT NULL DEFAULT 3 CHECK (max_retries >= 0),
    connect_timeout_ms   int NOT NULL DEFAULT 10000 CHECK (connect_timeout_ms > 0),
    request_timeout_ms   int NOT NULL DEFAULT 300000 CHECK (request_timeout_ms > 0),
    bandwidth_limit_bps  bigint NOT NULL DEFAULT 0 CHECK (bandwidth_limit_bps >= 0),
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CHECK (
      target_type = 'local'
      OR (bucket IS NOT NULL AND bucket <> '')
    )
);

CREATE TABLE dbbackup.backup_sets (
    name               text PRIMARY KEY,
    storage_target     text NOT NULL REFERENCES dbbackup.storage_targets(name),
    on_unsafe_failover text NOT NULL DEFAULT 'full_now'
                       CHECK (on_unsafe_failover IN ('full_now', 'warn_only', 'pause')),
    enabled            boolean NOT NULL DEFAULT true,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE dbbackup.backup_set_databases (
    backup_set text NOT NULL REFERENCES dbbackup.backup_sets(name) ON DELETE CASCADE,
    dbname     text NOT NULL,
    active     boolean NOT NULL DEFAULT true,
    dropped    boolean NOT NULL DEFAULT false,
    added_at   timestamptz NOT NULL DEFAULT now(),
    removed_at timestamptz,
    PRIMARY KEY (backup_set, dbname)
);

CREATE TABLE dbbackup.backup_artifacts (
    backup_id          uuid PRIMARY KEY,
    backup_set         text,
    storage_target     text NOT NULL REFERENCES dbbackup.storage_targets(name),
    dbname             text NOT NULL,
    backup_type        text NOT NULL CHECK (backup_type IN ('full', 'differential', 'log')),
    mode               text NOT NULL CHECK (mode IN ('simple', 'full')),
    chain_id           uuid NOT NULL,
    previous_backup_id uuid,
    object_key         text NOT NULL,
    object_uri         text NOT NULL,
    manifest_key       text NOT NULL,
    manifest_uri       text NOT NULL,
    start_lsn          pg_lsn NOT NULL,
    stop_lsn           pg_lsn NOT NULL,
    base_lsn           pg_lsn NOT NULL,
    range_start_time   timestamptz NOT NULL,
    range_end_time     timestamptz NOT NULL,
    size_bytes         bigint NOT NULL CHECK (size_bytes >= 0),
    sha256             text NOT NULL,
    status             text NOT NULL DEFAULT 'available'
                       CHECK (status IN ('available', 'missing', 'deleted', 'failed')),
    encrypted          boolean NOT NULL DEFAULT false,
    local_path         text,
    inserted_at        timestamptz NOT NULL DEFAULT now(),
    UNIQUE (storage_target, object_key),
    UNIQUE (storage_target, manifest_key)
);

CREATE INDEX pg_dbbackup_artifacts_search_idx
    ON dbbackup.backup_artifacts(storage_target, dbname, chain_id, range_end_time);

CREATE TABLE dbbackup.storage_uploads (
    upload_id        text PRIMARY KEY,
    storage_target   text NOT NULL REFERENCES dbbackup.storage_targets(name),
    object_key       text NOT NULL,
    backup_id        uuid,
    status           text NOT NULL DEFAULT 'running'
                     CHECK (status IN ('running', 'completed', 'aborted', 'failed')),
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    error_message    text
);

CREATE TABLE dbbackup.backup_events (
    event_id       bigserial PRIMARY KEY,
    severity       text NOT NULL DEFAULT 'info'
                   CHECK (severity IN ('info', 'warning', 'error')),
    event_type     text NOT NULL,
    backup_set     text,
    dbname         text,
    backup_id      uuid,
    message        text NOT NULL,
    detail         jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at     timestamptz NOT NULL DEFAULT now()
);

CREATE FUNCTION dbbackup.pg_dbbackup_notify_event()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    PERFORM pg_notify(
        'dbbackup_events',
        jsonb_build_object(
            'event_id', NEW.event_id,
            'severity', NEW.severity,
            'event_type', NEW.event_type,
            'backup_set', NEW.backup_set,
            'dbname', NEW.dbname,
            'backup_id', NEW.backup_id,
            'message', NEW.message,
            'created_at', NEW.created_at
        )::text
    );
    RETURN NEW;
END
$$;

CREATE TRIGGER pg_dbbackup_notify_event
AFTER INSERT ON dbbackup.backup_events
FOR EACH ROW EXECUTE FUNCTION dbbackup.pg_dbbackup_notify_event();

CREATE TABLE dbbackup.backup_schedules (
    schedule_id   uuid PRIMARY KEY,
    backup_set    text NOT NULL REFERENCES dbbackup.backup_sets(name) ON DELETE CASCADE,
    name          text NOT NULL,
    backup_type   text NOT NULL CHECK (backup_type IN ('full', 'differential', 'log')),
    cron          text,
    every         interval,
    timezone      text NOT NULL DEFAULT 'UTC',
    window_start  time,
    window_end    time,
    enabled       boolean NOT NULL DEFAULT true,
    last_run_at   timestamptz,
    run_count     bigint NOT NULL DEFAULT 0,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    UNIQUE (backup_set, name),
    CHECK (cron IS NOT NULL OR every IS NOT NULL)
);

CREATE TABLE dbbackup.retention_policies (
    backup_set            text PRIMARY KEY REFERENCES dbbackup.backup_sets(name) ON DELETE CASCADE,
    keep_incrementals_for interval NOT NULL DEFAULT interval '14 days',
    keep_fulls_for        interval NOT NULL DEFAULT interval '31 days',
    keep_min_full_chains  int NOT NULL DEFAULT 2 CHECK (keep_min_full_chains >= 1),
    delete_remote         boolean NOT NULL DEFAULT false,
    updated_at            timestamptz NOT NULL DEFAULT now()
);

CREATE FUNCTION dbbackup.pg_dbbackup_s3_create_bucket(storage_target text)
RETURNS void
AS 'MODULE_PATHNAME', 'pg_dbbackup_s3_create_bucket'
LANGUAGE C VOLATILE STRICT;

CREATE FUNCTION dbbackup.pg_dbbackup_s3_object_exists(
    storage_target text,
    object_key     text
) RETURNS boolean
AS 'MODULE_PATHNAME', 'pg_dbbackup_s3_object_exists'
LANGUAGE C VOLATILE STRICT;

CREATE FUNCTION dbbackup.pg_dbbackup_s3_delete_object(
    storage_target text,
    object_key     text
) RETURNS void
AS 'MODULE_PATHNAME', 'pg_dbbackup_s3_delete_object'
LANGUAGE C VOLATILE STRICT;

CREATE FUNCTION dbbackup.pg_dbbackup_refresh_storage_catalog(
    storage_target text,
    dbname         text
) RETURNS int
AS 'MODULE_PATHNAME', 'pg_dbbackup_refresh_storage_catalog'
LANGUAGE C VOLATILE STRICT;

CREATE FUNCTION dbbackup.pg_dbbackup_to_storage(
    dbname         text,
    type           text    DEFAULT 'full',
    storage_target text    DEFAULT NULL,
    backup_set     text    DEFAULT NULL,
    compress       boolean DEFAULT true,
    password       text    DEFAULT NULL,
    base_backup_id uuid    DEFAULT NULL
) RETURNS uuid
AS 'MODULE_PATHNAME', 'pg_dbbackup_to_storage'
LANGUAGE C VOLATILE;

CREATE FUNCTION dbbackup.pg_dbrestore_from_storage_impl(
    dbname         text,
    storage_target text,
    target_db      text DEFAULT NULL,
    stop_at        timestamptz DEFAULT NULL,
    password       text DEFAULT NULL
) RETURNS void
AS 'MODULE_PATHNAME', 'pg_dbrestore_from_storage_impl'
LANGUAGE C VOLATILE;

CREATE FUNCTION dbbackup.pg_dbrestore_from_storage(
    dbname         text,
    storage_target text,
    target_db      text DEFAULT NULL,
    stop_at        timestamptz DEFAULT NULL,
    password       text DEFAULT NULL
) RETURNS void
LANGUAGE plpgsql
VOLATILE
AS $$
BEGIN
    PERFORM dbbackup.pg_dbbackup_refresh_storage_catalog($2, $1);
    PERFORM dbbackup.pg_dbrestore_from_storage_impl($1, $2, $3, $4, $5);
END
$$;

CREATE FUNCTION dbbackup.pg_dbrestore_at(
    dbname         text,
    target_db      text,
    stop_at        timestamptz,
    storage_target text,
    password       text DEFAULT NULL
) RETURNS void
LANGUAGE sql
VOLATILE
AS $$
    SELECT dbbackup.pg_dbrestore_from_storage(
        dbname := $1,
        storage_target := $4,
        target_db := $2,
        stop_at := $3,
        password := $5
    )
$$;

CREATE FUNCTION dbbackup.create_s3_target(
    name                 text,
    bucket               text,
    prefix               text DEFAULT '',
    region               text DEFAULT NULL,
    endpoint_url         text DEFAULT NULL,
    force_path_style     boolean DEFAULT false,
    encryption           text DEFAULT 'none',
    kms_key_id           text DEFAULT NULL,
    object_lock_mode     text DEFAULT 'off',
    object_lock_retain_until text DEFAULT NULL,
    max_retries          int DEFAULT 3,
    connect_timeout_ms   int DEFAULT 10000,
    request_timeout_ms   int DEFAULT 300000,
    bandwidth_limit_bps  bigint DEFAULT 0
) RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO dbbackup.storage_targets(
        name, target_type, bucket, prefix, region, endpoint_url,
        force_path_style, encryption, kms_key_id, object_lock_mode,
        object_lock_retain_until, max_retries, connect_timeout_ms,
        request_timeout_ms, bandwidth_limit_bps, updated_at)
    VALUES (
        $1, 's3-compatible', $2, COALESCE($3, ''), $4,
        $5, $6, $7, $8,
        $9, $10, $11,
        $12, $13, $14, now())
    ON CONFLICT ON CONSTRAINT storage_targets_pkey DO UPDATE SET
        bucket = EXCLUDED.bucket,
        prefix = EXCLUDED.prefix,
        region = EXCLUDED.region,
        endpoint_url = EXCLUDED.endpoint_url,
        force_path_style = EXCLUDED.force_path_style,
        encryption = EXCLUDED.encryption,
        kms_key_id = EXCLUDED.kms_key_id,
        object_lock_mode = EXCLUDED.object_lock_mode,
        object_lock_retain_until = EXCLUDED.object_lock_retain_until,
        max_retries = EXCLUDED.max_retries,
        connect_timeout_ms = EXCLUDED.connect_timeout_ms,
        request_timeout_ms = EXCLUDED.request_timeout_ms,
        bandwidth_limit_bps = EXCLUDED.bandwidth_limit_bps,
        updated_at = now();
END
$$;

CREATE FUNCTION dbbackup.create_backup_set(
    name               text,
    storage_target     text,
    on_unsafe_failover text DEFAULT 'full_now'
) RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO dbbackup.backup_sets(name, storage_target, on_unsafe_failover, updated_at)
    VALUES ($1, $2, $3, now())
    ON CONFLICT ON CONSTRAINT backup_sets_pkey DO UPDATE SET
        storage_target = EXCLUDED.storage_target,
        on_unsafe_failover = EXCLUDED.on_unsafe_failover,
        enabled = true,
        updated_at = now();

    INSERT INTO dbbackup.retention_policies(backup_set)
    VALUES ($1)
    ON CONFLICT (backup_set) DO NOTHING;
END
$$;

CREATE FUNCTION dbbackup.add_database_to_backup_set(
    backup_set text,
    dbname     text
) RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO dbbackup.backup_set_databases(backup_set, dbname, active, dropped, removed_at)
    VALUES ($1, $2, true, false, NULL)
    ON CONFLICT ON CONSTRAINT backup_set_databases_pkey DO UPDATE SET
        active = true,
        dropped = false,
        removed_at = NULL;
END
$$;

CREATE FUNCTION dbbackup.remove_database_from_backup_set(
    backup_set   text,
    dbname       text,
    close_chain  boolean DEFAULT true,
    keep_history boolean DEFAULT true
) RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    target text;
BEGIN
    SELECT storage_target INTO target
      FROM dbbackup.backup_sets
     WHERE name = backup_set;
    IF target IS NULL THEN
        RAISE EXCEPTION 'backup set "%" does not exist', backup_set;
    END IF;

    IF close_chain
       AND EXISTS (SELECT 1 FROM pg_database WHERE datname = dbname)
       AND dbbackup.pg_dbbackup_get_mode(dbname) = 'full' THEN
        BEGIN
            PERFORM dbbackup.pg_dbbackup_to_storage(
                dbname := remove_database_from_backup_set.dbname,
                type := 'log',
                storage_target := target,
                backup_set := remove_database_from_backup_set.backup_set
            );
        EXCEPTION WHEN others THEN
            INSERT INTO dbbackup.backup_events(severity, event_type, backup_set, dbname, message)
            VALUES ('warning', 'close_chain_failed',
                    remove_database_from_backup_set.backup_set,
                    remove_database_from_backup_set.dbname, SQLERRM);
        END;
    END IF;

    IF keep_history THEN
        UPDATE dbbackup.backup_set_databases
           SET active = false, removed_at = now()
         WHERE backup_set = remove_database_from_backup_set.backup_set
           AND dbname = remove_database_from_backup_set.dbname;
    ELSE
        DELETE FROM dbbackup.backup_set_databases
         WHERE backup_set = remove_database_from_backup_set.backup_set
           AND dbname = remove_database_from_backup_set.dbname;
    END IF;
END
$$;

CREATE FUNCTION dbbackup.set_backup_set_databases(
    backup_set text,
    databases  text[]
) RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    db text;
BEGIN
    UPDATE dbbackup.backup_set_databases b
       SET active = false, removed_at = now()
     WHERE b.backup_set = set_backup_set_databases.backup_set
       AND NOT (b.dbname = ANY(databases));

    FOREACH db IN ARRAY databases LOOP
        PERFORM dbbackup.add_database_to_backup_set(backup_set, db);
    END LOOP;
END
$$;

CREATE FUNCTION dbbackup.list_backup_set_databases(backup_set text)
RETURNS TABLE(dbname text, active boolean, dropped boolean, added_at timestamptz, removed_at timestamptz)
LANGUAGE sql
STABLE
AS $$
    SELECT dbname, active, dropped, added_at, removed_at
      FROM dbbackup.backup_set_databases
     WHERE backup_set = $1
     ORDER BY dbname
$$;

CREATE FUNCTION dbbackup.create_schedule(
    backup_set   text,
    name         text,
    backup_type  text,
    cron         text DEFAULT NULL,
    every        interval DEFAULT NULL,
    timezone     text DEFAULT 'UTC',
    window_start time DEFAULT NULL,
    window_end   time DEFAULT NULL
) RETURNS uuid
LANGUAGE plpgsql
AS $$
DECLARE
    id uuid := md5(random()::text || clock_timestamp()::text)::uuid;
BEGIN
    INSERT INTO dbbackup.backup_schedules(
        schedule_id, backup_set, name, backup_type, cron, every, timezone,
        window_start, window_end)
    VALUES (id, $1, $2, $3, $4, $5, $6, $7, $8);
    RETURN id;
END
$$;

CREATE FUNCTION dbbackup.alter_schedule(
    backup_set   text,
    name         text,
    backup_type  text DEFAULT NULL,
    cron         text DEFAULT NULL,
    every        interval DEFAULT NULL,
    timezone     text DEFAULT NULL,
    window_start time DEFAULT NULL,
    window_end   time DEFAULT NULL
) RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE dbbackup.backup_schedules s
       SET backup_type = COALESCE(alter_schedule.backup_type, s.backup_type),
           cron = COALESCE(alter_schedule.cron, s.cron),
           every = COALESCE(alter_schedule.every, s.every),
           timezone = COALESCE(alter_schedule.timezone, s.timezone),
           window_start = COALESCE(alter_schedule.window_start, s.window_start),
           window_end = COALESCE(alter_schedule.window_end, s.window_end),
           updated_at = now()
     WHERE s.backup_set = alter_schedule.backup_set
       AND s.name = alter_schedule.name;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'schedule "%.%" does not exist', backup_set, name;
    END IF;
END
$$;

CREATE FUNCTION dbbackup.pause_schedule(backup_set text, name text)
RETURNS void
LANGUAGE sql
AS $$
    UPDATE dbbackup.backup_schedules
       SET enabled = false, updated_at = now()
     WHERE backup_set = $1 AND name = $2
$$;

CREATE FUNCTION dbbackup.resume_schedule(backup_set text, name text)
RETURNS void
LANGUAGE sql
AS $$
    UPDATE dbbackup.backup_schedules
       SET enabled = true, updated_at = now()
     WHERE backup_set = $1 AND name = $2
$$;

CREATE FUNCTION dbbackup.drop_schedule(backup_set text, name text)
RETURNS void
LANGUAGE sql
AS $$
    DELETE FROM dbbackup.backup_schedules
     WHERE backup_set = $1 AND name = $2
$$;

CREATE FUNCTION dbbackup.pg_dbbackup_cron_field_matches(
    field     text,
    value     int,
    min_value int,
    max_value int
) RETURNS boolean
LANGUAGE plpgsql
IMMUTABLE
STRICT
AS $$
DECLARE
    part text;
    step int;
    n int;
BEGIN
    IF field = '*' THEN
        RETURN true;
    END IF;

    IF field LIKE '*/%' THEN
        step := substring(field from 3)::int;
        IF step <= 0 THEN
            RETURN false;
        END IF;
        RETURN ((value - min_value) % step) = 0;
    END IF;

    FOREACH part IN ARRAY string_to_array(field, ',') LOOP
        IF part ~ '^\d+$' THEN
            n := part::int;
            IF n BETWEEN min_value AND max_value AND value = n THEN
                RETURN true;
            END IF;
            IF max_value = 7 AND n = 7 AND value = 0 THEN
                RETURN true;
            END IF;
        END IF;
    END LOOP;

    RETURN false;
EXCEPTION WHEN others THEN
    RETURN false;
END
$$;

CREATE FUNCTION dbbackup.pg_dbbackup_cron_due(
    cron     text,
    timezone text,
    due_at   timestamptz
) RETURNS boolean
LANGUAGE plpgsql
IMMUTABLE
STRICT
AS $$
DECLARE
    f text[];
    local_ts timestamp;
    dow int;
BEGIN
    f := regexp_split_to_array(btrim(cron), '\s+');
    IF array_length(f, 1) <> 5 THEN
        RETURN false;
    END IF;

    local_ts := due_at AT TIME ZONE timezone;
    dow := extract(dow from local_ts)::int;

    RETURN dbbackup.pg_dbbackup_cron_field_matches(f[1], extract(minute from local_ts)::int, 0, 59)
       AND dbbackup.pg_dbbackup_cron_field_matches(f[2], extract(hour from local_ts)::int, 0, 23)
       AND dbbackup.pg_dbbackup_cron_field_matches(f[3], extract(day from local_ts)::int, 1, 31)
       AND dbbackup.pg_dbbackup_cron_field_matches(f[4], extract(month from local_ts)::int, 1, 12)
       AND dbbackup.pg_dbbackup_cron_field_matches(f[5], dow, 0, 7);
END
$$;

CREATE FUNCTION dbbackup.pg_dbbackup_run_due_schedules(
    due_at timestamptz DEFAULT clock_timestamp()
) RETURNS int
LANGUAGE plpgsql
VOLATILE
AS $$
DECLARE
    sched record;
    member record;
    local_time time;
    last_local_minute timestamp;
    due_local_minute timestamp;
    in_window boolean;
    is_due boolean;
    made int := 0;
BEGIN
    IF pg_is_in_recovery() THEN
        RETURN 0;
    END IF;

    FOR sched IN
        SELECT s.*, bs.storage_target
          FROM dbbackup.backup_schedules s
          JOIN dbbackup.backup_sets bs ON bs.name = s.backup_set
         WHERE s.enabled
           AND bs.enabled
         ORDER BY s.backup_set, s.name
    LOOP
        local_time := (due_at AT TIME ZONE sched.timezone)::time;
        in_window := true;
        IF sched.window_start IS NOT NULL AND sched.window_end IS NOT NULL THEN
            IF sched.window_start <= sched.window_end THEN
                in_window := local_time >= sched.window_start
                             AND local_time < sched.window_end;
            ELSE
                in_window := local_time >= sched.window_start
                             OR local_time < sched.window_end;
            END IF;
        END IF;
        IF NOT in_window THEN
            CONTINUE;
        END IF;

        is_due := false;
        IF sched.every IS NOT NULL THEN
            is_due := sched.last_run_at IS NULL
                      OR due_at >= sched.last_run_at + sched.every;
        ELSIF sched.cron IS NOT NULL THEN
            is_due := dbbackup.pg_dbbackup_cron_due(
                sched.cron, sched.timezone, due_at);
            IF is_due AND sched.last_run_at IS NOT NULL THEN
                last_local_minute := date_trunc(
                    'minute', sched.last_run_at AT TIME ZONE sched.timezone);
                due_local_minute := date_trunc(
                    'minute', due_at AT TIME ZONE sched.timezone);
                is_due := last_local_minute < due_local_minute;
            END IF;
        END IF;
        IF NOT is_due THEN
            CONTINUE;
        END IF;

        FOR member IN
            SELECT dbname
              FROM dbbackup.backup_set_databases
             WHERE backup_set = sched.backup_set
               AND active
               AND NOT dropped
             ORDER BY dbname
        LOOP
            IF NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = member.dbname) THEN
                UPDATE dbbackup.backup_set_databases
                   SET active = false, dropped = true, removed_at = now()
                 WHERE backup_set = sched.backup_set
                   AND dbname = member.dbname;
                INSERT INTO dbbackup.backup_events(severity, event_type, backup_set, dbname, message)
                VALUES ('warning', 'database_missing', sched.backup_set, member.dbname,
                        'scheduled database no longer exists');
                CONTINUE;
            END IF;

            BEGIN
                IF sched.backup_type = 'full'
                   AND dbbackup.pg_dbbackup_get_mode(member.dbname) = 'full'
                   AND EXISTS (
                       SELECT 1
                         FROM dbbackup.backup_artifacts
                        WHERE backup_set = sched.backup_set
                          AND dbname = member.dbname
                          AND status = 'available'
                   ) THEN
                    BEGIN
                        PERFORM dbbackup.pg_dbbackup_to_storage(
                            dbname := member.dbname,
                            type := 'log',
                            storage_target := sched.storage_target,
                            backup_set := sched.backup_set
                        );
                    EXCEPTION WHEN others THEN
                        INSERT INTO dbbackup.backup_events(
                            severity, event_type, backup_set, dbname, message)
                        VALUES ('warning', 'scheduled_tail_log_failed',
                                sched.backup_set, member.dbname, SQLERRM);
                    END;
                END IF;

                PERFORM dbbackup.pg_dbbackup_to_storage(
                    dbname := member.dbname,
                    type := sched.backup_type,
                    storage_target := sched.storage_target,
                    backup_set := sched.backup_set
                );
                made := made + 1;
            EXCEPTION WHEN others THEN
                INSERT INTO dbbackup.backup_events(
                    severity, event_type, backup_set, dbname, message)
                VALUES ('error', 'scheduled_backup_failed',
                        sched.backup_set, member.dbname, SQLERRM);
            END;
        END LOOP;

        UPDATE dbbackup.backup_schedules
           SET last_run_at = due_at,
               run_count = run_count + 1,
               updated_at = now()
         WHERE schedule_id = sched.schedule_id;
    END LOOP;

    RETURN made;
END
$$;

CREATE FUNCTION dbbackup.pg_dbbackup_search(
    dbname         text,
    storage_target text,
    from_time      timestamptz DEFAULT NULL,
    to_time        timestamptz DEFAULT NULL
) RETURNS TABLE(
    backup_id uuid,
    backup_type text,
    chain_id uuid,
    range_start_time timestamptz,
    range_end_time timestamptz,
    object_uri text,
    manifest_uri text,
    size_bytes bigint,
    status text
)
LANGUAGE plpgsql
VOLATILE
AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
          FROM dbbackup.backup_artifacts a
         WHERE a.dbname = $1
           AND a.storage_target = $2
         LIMIT 1
    ) THEN
        PERFORM dbbackup.pg_dbbackup_refresh_storage_catalog(
            $2,
            $1
        );
    END IF;

    RETURN QUERY
    SELECT a.backup_id, a.backup_type, a.chain_id,
           a.range_start_time, a.range_end_time,
           a.object_uri, a.manifest_uri, a.size_bytes, a.status
      FROM dbbackup.backup_artifacts a
     WHERE a.dbname = $1
       AND a.storage_target = $2
       AND ($3 IS NULL OR a.range_end_time >= $3)
       AND ($4 IS NULL OR a.range_start_time <= $4)
     ORDER BY a.range_end_time, a.inserted_at;
END
$$;

CREATE FUNCTION dbbackup._search_backups_impl(
    database_name  text,
    from_time      timestamptz DEFAULT NULL,
    to_time        timestamptz DEFAULT NULL,
    storage_target text DEFAULT NULL
) RETURNS TABLE(
    chain_id uuid,
    dbname text,
    range_start timestamptz,
    range_end timestamptz,
    full_uri text,
    diff_count bigint,
    log_count bigint,
    restorable boolean,
    gap_reason text,
    required_artifacts text[]
)
LANGUAGE plpgsql
VOLATILE
AS $$
BEGIN
    IF $4 IS NOT NULL
       AND NOT EXISTS (
           SELECT 1
             FROM dbbackup.backup_artifacts a
            WHERE a.dbname = $1
              AND a.storage_target = $4
            LIMIT 1
       ) THEN
        PERFORM dbbackup.pg_dbbackup_refresh_storage_catalog(
            $4,
            $1
        );
    END IF;

    RETURN QUERY
    WITH candidate AS (
        SELECT *
          FROM dbbackup.backup_artifacts a
         WHERE a.dbname = $1
           AND ($4 IS NULL OR a.storage_target = $4)
           AND ($2 IS NULL OR a.range_end_time >= $2)
           AND ($3 IS NULL OR a.range_start_time <= $3)
    ),
    checked AS (
        SELECT c.*,
               dbbackup.pg_dbbackup_s3_object_exists(c.storage_target, c.object_key)
               AND dbbackup.pg_dbbackup_s3_object_exists(c.storage_target, c.manifest_key)
               AS remote_available
          FROM candidate c
    ),
    grouped AS (
        SELECT c.chain_id,
               min(c.range_start_time) AS range_start,
               max(c.range_end_time) AS range_end,
               max(c.object_uri) FILTER (WHERE c.backup_type = 'full') AS full_uri,
               count(*) FILTER (WHERE c.backup_type = 'differential') AS diff_count,
               count(*) FILTER (WHERE c.backup_type = 'log') AS log_count,
               bool_and(c.status = 'available' AND c.remote_available) AS all_available,
               bool_or(NOT c.remote_available) AS any_remote_missing,
               array_agg(c.object_uri ORDER BY c.range_end_time, c.inserted_at) AS required_artifacts
          FROM checked c
         GROUP BY c.chain_id
    )
    SELECT g.chain_id,
           $1 AS dbname,
           g.range_start,
           g.range_end,
           g.full_uri,
           g.diff_count,
           g.log_count,
           g.all_available AND g.full_uri IS NOT NULL AS restorable,
           CASE
             WHEN g.full_uri IS NULL THEN 'missing full backup'
             WHEN g.any_remote_missing THEN 'one or more remote artifacts missing'
             WHEN NOT g.all_available THEN 'one or more artifacts unavailable'
             ELSE NULL
           END AS gap_reason,
           g.required_artifacts
      FROM grouped g
     ORDER BY g.range_end DESC;
END
$$;

CREATE FUNCTION dbbackup.search_backups(
    dbname         text,
    from_time      timestamptz DEFAULT NULL,
    to_time        timestamptz DEFAULT NULL,
    storage_target text DEFAULT NULL
) RETURNS TABLE(
    chain_id uuid,
    dbname text,
    range_start timestamptz,
    range_end timestamptz,
    full_uri text,
    diff_count bigint,
    log_count bigint,
    restorable boolean,
    gap_reason text,
    required_artifacts text[]
)
LANGUAGE sql
VOLATILE
AS $$
    SELECT *
      FROM dbbackup._search_backups_impl($1, $2, $3, $4)
$$;

CREATE FUNCTION dbbackup.set_retention_policy(
    backup_set            text,
    keep_logs_for         interval DEFAULT interval '14 days',
    keep_fulls_for        interval DEFAULT interval '31 days',
    keep_min_full_chains  int DEFAULT 2,
    delete_from_storage   boolean DEFAULT false
) RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO dbbackup.retention_policies(
        backup_set, keep_incrementals_for, keep_fulls_for,
        keep_min_full_chains, delete_remote, updated_at)
    VALUES ($1, $2, $3, $4, $5, now())
    ON CONFLICT (backup_set) DO UPDATE SET
        keep_incrementals_for = EXCLUDED.keep_incrementals_for,
        keep_fulls_for = EXCLUDED.keep_fulls_for,
        keep_min_full_chains = EXCLUDED.keep_min_full_chains,
        delete_remote = EXCLUDED.delete_remote,
        updated_at = now();
END
$$;

CREATE FUNCTION dbbackup.pg_dbbackup_retention_plan(
    backup_set text
) RETURNS TABLE(
    backup_id uuid,
    dbname text,
    backup_type text,
    chain_id uuid,
    object_uri text,
    manifest_uri text,
    reason text,
    delete_remote boolean
)
LANGUAGE sql
STABLE
AS $$
    WITH policy AS (
        SELECT *
          FROM dbbackup.retention_policies
         WHERE backup_set = $1
    ),
    ranked_fulls AS (
        SELECT chain_id,
               dense_rank() OVER (PARTITION BY dbname ORDER BY range_end_time DESC) AS full_rank
          FROM dbbackup.backup_artifacts
         WHERE backup_set = $1
           AND backup_type = 'full'
           AND status = 'available'
    ),
    newest_chains AS (
        SELECT r.chain_id
          FROM ranked_fulls r
          CROSS JOIN policy p
         WHERE r.full_rank <= p.keep_min_full_chains
    )
    SELECT a.backup_id,
           a.dbname,
           a.backup_type,
           a.chain_id,
           a.object_uri,
           a.manifest_uri,
           CASE
             WHEN a.backup_type = 'full' THEN 'full history expired'
             ELSE 'incremental PITR window expired'
           END,
           COALESCE(p.delete_remote, false)
      FROM dbbackup.backup_artifacts a
      CROSS JOIN policy p
     WHERE a.backup_set = $1
       AND a.status = 'available'
       AND a.chain_id NOT IN (SELECT chain_id FROM newest_chains)
       AND (
            (a.backup_type = 'full'
             AND a.range_end_time < now() - p.keep_fulls_for)
            OR
            (a.backup_type <> 'full'
             AND a.range_end_time < now() - p.keep_incrementals_for)
       )
     ORDER BY a.range_end_time
$$;

CREATE FUNCTION dbbackup.pg_dbbackup_apply_retention(
    backup_set text,
    dry_run boolean DEFAULT true
) RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    row record;
    n int := 0;
BEGIN
    FOR row IN SELECT * FROM dbbackup.pg_dbbackup_retention_plan($1) LOOP
        n := n + 1;
        IF NOT dry_run THEN
            IF row.delete_remote THEN
                PERFORM dbbackup.pg_dbbackup_s3_delete_object(
                    (SELECT storage_target FROM dbbackup.backup_artifacts WHERE backup_id = row.backup_id),
                    (SELECT object_key FROM dbbackup.backup_artifacts WHERE backup_id = row.backup_id)
                );
                PERFORM dbbackup.pg_dbbackup_s3_delete_object(
                    (SELECT storage_target FROM dbbackup.backup_artifacts WHERE backup_id = row.backup_id),
                    (SELECT manifest_key FROM dbbackup.backup_artifacts WHERE backup_id = row.backup_id)
                );
            END IF;
            UPDATE dbbackup.backup_artifacts
               SET status = 'deleted'
             WHERE backup_id = row.backup_id;
            INSERT INTO dbbackup.backup_events(severity, event_type, backup_set, dbname, backup_id, message)
            VALUES ('info', 'retention_deleted', $1, row.dbname, row.backup_id, row.reason);
        END IF;
    END LOOP;
    RETURN n;
END
$$;

CREATE FUNCTION dbbackup.restore_drill(
    dbname         text,
    storage_target text,
    stop_at        timestamptz,
    target_db      text,
    password       text DEFAULT NULL
) RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    PERFORM dbbackup.pg_dbrestore_at(
        dbname := $1,
        target_db := $4,
        stop_at := $3,
        storage_target := $2,
        password := $5
    );
    INSERT INTO dbbackup.backup_events(severity, event_type, dbname, message)
    VALUES ('info', 'restore_drill_completed', $1,
            format('restore drill completed into database %s', $4));
END
$$;

CREATE FUNCTION dbbackup.restore_drill_plan(
    backup_set text,
    sample_size int DEFAULT 3
) RETURNS TABLE(dbname text, storage_target text, suggested_stop_at timestamptz)
LANGUAGE sql
STABLE
AS $$
    SELECT d.dbname, s.storage_target, now() - interval '5 minutes'
      FROM dbbackup.backup_set_databases d
      JOIN dbbackup.backup_sets s ON s.name = d.backup_set
     WHERE d.backup_set = $1
       AND d.active
     ORDER BY d.dbname
     LIMIT $2
$$;

CREATE FUNCTION dbbackup.pg_dbbackup_reseed_if_needed(
    backup_set text
) RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    row record;
    target text;
    policy text;
    made int := 0;
    unsafe boolean;
BEGIN
    SELECT storage_target, on_unsafe_failover INTO target, policy
      FROM dbbackup.backup_sets
     WHERE name = backup_set AND enabled;
    IF target IS NULL THEN
        RAISE EXCEPTION 'backup set "%" does not exist or is disabled', backup_set;
    END IF;

    FOR row IN
        SELECT d.dbname
          FROM dbbackup.backup_set_databases d
         WHERE d.backup_set = pg_dbbackup_reseed_if_needed.backup_set
           AND d.active
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = row.dbname) THEN
            UPDATE dbbackup.backup_set_databases
               SET active = false, dropped = true, removed_at = now()
             WHERE backup_set = pg_dbbackup_reseed_if_needed.backup_set
               AND dbname = row.dbname;
            INSERT INTO dbbackup.backup_events(severity, event_type, backup_set, dbname, message)
            VALUES ('warning', 'database_missing', backup_set, row.dbname,
                    'database is still listed in backup set but no longer exists');
            CONTINUE;
        END IF;

        SELECT COALESCE((
            SELECT NOT (
                slot_exists
                AND failover
                AND NOT temporary
                AND invalidation_reason IS NULL
            )
            FROM dbbackup.pg_dbbackup_failover_slot_status(row.dbname)
        ), true) INTO unsafe;

        IF unsafe THEN
            IF policy = 'full_now' THEN
                PERFORM dbbackup.pg_dbbackup_to_storage(
                    dbname := row.dbname,
                    type := 'full',
                    storage_target := target,
                    backup_set := pg_dbbackup_reseed_if_needed.backup_set
                );
                made := made + 1;
            ELSE
                INSERT INTO dbbackup.backup_events(severity, event_type, backup_set, dbname, message)
                VALUES ('warning', 'unsafe_failover_slot',
                        pg_dbbackup_reseed_if_needed.backup_set, row.dbname,
                        'logical PITR slot is missing or unsafe; new FULL backup required');
            END IF;
        END IF;
    END LOOP;

    RETURN made;
END
$$;

-- Async backup job tracking
CREATE TABLE dbbackup.backup_jobs (
    backup_id     uuid PRIMARY KEY,
    dbname        text NOT NULL,
    destination   text NOT NULL DEFAULT 'file'
                 CHECK (destination IN ('file', 'storage')),
    filepath      text,
    storage_target text,
    backup_set    text,
    type          text NOT NULL,
    compress      boolean NOT NULL,
    has_password  boolean NOT NULL,
    base_filepath text,
    base_backup_id uuid,
    status        text NOT NULL DEFAULT 'pending'
                  CHECK (status IN ('pending', 'running', 'completed', 'failed')),
    progress      int NOT NULL DEFAULT 0,
    error_msg     text,
    started_at    timestamptz,
    completed_at  timestamptz,
    requester_pid int,
    created_at    timestamptz NOT NULL DEFAULT now()
);

-- Launch a backup in a background worker; returns the job UUID immediately.
CREATE FUNCTION dbbackup.pg_dbbackup_async(
    dbname        text,
    filepath      text,
    type          text    DEFAULT 'full',
    compress      boolean DEFAULT true,
    password      text    DEFAULT NULL,
    base_filepath text    DEFAULT NULL
) RETURNS uuid
AS 'MODULE_PATHNAME', 'pg_dbbackup_async'
LANGUAGE C VOLATILE;

CREATE FUNCTION dbbackup.pg_dbbackup_to_storage_async(
    dbname         text,
    type           text    DEFAULT 'full',
    storage_target text    DEFAULT NULL,
    backup_set     text    DEFAULT NULL,
    compress       boolean DEFAULT true,
    base_backup_id uuid    DEFAULT NULL
) RETURNS uuid
AS 'MODULE_PATHNAME', 'pg_dbbackup_to_storage_async'
LANGUAGE C VOLATILE;

-- Read the current status of an async backup job.
CREATE FUNCTION dbbackup.pg_dbbackup_status(backup_id uuid)
RETURNS TABLE (
    status        text,
    progress_pct  int,
    error_message text,
    started_at    timestamptz,
    completed_at  timestamptz
)
AS $$
    SELECT status, progress, error_msg, started_at, completed_at
      FROM dbbackup.backup_jobs WHERE backup_id = $1
$$ LANGUAGE sql STABLE;

-- Block until a backup job reaches a terminal state or the timeout expires.
CREATE FUNCTION dbbackup.pg_dbbackup_wait(
    backup_id uuid,
    timeout_secs int DEFAULT 300
) RETURNS text
AS 'MODULE_PATHNAME', 'pg_dbbackup_wait'
LANGUAGE C VOLATILE STRICT;
