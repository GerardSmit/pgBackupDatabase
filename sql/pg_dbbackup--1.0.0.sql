\echo Use "CREATE EXTENSION pg_dbbackup" to load this file. \quit

-- Mode configuration table
CREATE TABLE dbbackup.db_config (
    db_oid    oid PRIMARY KEY,
    db_name   text NOT NULL,
    mode      text NOT NULL DEFAULT 'simple'
        CHECK (mode IN ('simple', 'full'))
);

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
