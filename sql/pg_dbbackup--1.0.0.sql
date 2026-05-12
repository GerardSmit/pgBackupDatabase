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

-- Async backup job tracking
CREATE TABLE dbbackup.backup_jobs (
    backup_id     uuid PRIMARY KEY,
    dbname        text NOT NULL,
    filepath      text NOT NULL,
    type          text NOT NULL,
    compress      boolean NOT NULL,
    has_password  boolean NOT NULL,
    base_filepath text,
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
