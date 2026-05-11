# pg_dbbackup

`pg_dbbackup` is a PostgreSQL 17 extension that performs **per-database**
online backups in two recovery models (`SIMPLE`, `FULL`) and three backup
types (`FULL`, `DIFFERENTIAL`, `LOG`), modelled on SQL Server's
`BACKUP DATABASE` / `BACKUP LOG` workflow. Backups are written as
self-contained `.bak` files with magic bytes at both ends, a JSON
header, optional zstd compression, and optional AES-256-GCM encryption.
Restore creates a temporary database, populates it from the `.bak`
chain, and atomically renames it over the target — existing databases
are replaced safely.

The extension targets the gap between `pg_basebackup` (cluster-wide,
physical) and `pg_dump` (logical, no PITR): a single database, online,
optionally encrypted, portable across machines via one file per backup.

## Status

This is a v1. The SIMPLE recovery model is feature-complete and fully
round-trips on stock heap tables, indexes, and most extensions
(pgvector, pg_textsearch). The FULL recovery model captures files and
WAL but **does not run a true WAL redo pass in-process** during restore
— see *Known limitations* below.

## Installation

### Build prerequisites

- PostgreSQL 17 server, with the development headers
  (`postgresql-server-dev-17` on Debian/Ubuntu)
- A C toolchain (`build-essential`)
- `libzstd-dev` — backup compression
- `libssl-dev` — AES-256-GCM and PBKDF2 via OpenSSL
- `libpq` (already present via `postgresql-server-dev-17`) — used by
  the restore path to issue `CREATE DATABASE` / `COPY FROM STDIN`
  from the running backend

The Makefile uses PGXS (`$(pg_config --pgxs)`) and links against
`-lzstd -lcrypto -lpq`.

### Build & install

```bash
make
sudo make install
```

This installs the shared library next to other PG modules and copies
`pg_dbbackup.control` plus `sql/pg_dbbackup--1.0.0.sql` into
`pg_config --sharedir`/extension.

### Cluster configuration

`pg_dbbackup` requires the cluster to be running with `wal_level =
replica` (or higher). Both SIMPLE and FULL modes rely on WAL records
being present.

### Enable in a database

The extension is **superuser-only** and **non-relocatable** — its
schema is fixed to `dbbackup`:

```sql
CREATE EXTENSION pg_dbbackup;
```

## Quickstart

The functions live in the `dbbackup` schema. All paths in the examples
below refer to a path on the **server**'s filesystem (the backend
process writes the file directly).

### 1. Pick a recovery model

```sql
-- SIMPLE is the default; switch a DB to FULL if you need PITR / LOG backups
SELECT dbbackup.pg_dbbackup_set_mode('app', 'full');
SELECT dbbackup.pg_dbbackup_get_mode('app');     -- 'full'
```

### 2. Take a FULL backup

```sql
SELECT dbbackup.pg_dbbackup(
    'app',                                       -- target database
    '/var/backups/app-2026-05-11-full.bak',      -- output path
    type     := 'full',
    compress := true,
    password := 's3cret');                       -- optional AES-256-GCM
```

### 3. Take a DIFFERENTIAL (cumulative since the FULL)

```sql
SELECT dbbackup.pg_dbbackup(
    'app',
    '/var/backups/app-2026-05-11-diff.bak',
    type          := 'differential',
    base_filepath := '/var/backups/app-2026-05-11-full.bak');
```

### 4. Take a LOG backup (FULL mode only; WAL-only, no data files)

```sql
SELECT dbbackup.pg_dbbackup(
    'app',
    '/var/backups/app-2026-05-11-1200.log.bak',
    type          := 'log',
    base_filepath := '/var/backups/app-2026-05-11-diff.bak');
```

### 5. Restore a chain

```sql
-- SIMPLE: FULL + at most one DIFFERENTIAL
SELECT dbbackup.pg_dbrestore(
    'app',                                       -- legacy first arg, ignored
    ARRAY['/var/backups/app-2026-05-11-full.bak',
          '/var/backups/app-2026-05-11-diff.bak']::text[],
    target_db := 'app',                          -- existing DB is dropped + replaced
    password  := 's3cret');

-- FULL mode: FULL + optional DIFFERENTIAL + zero or more LOGs
SELECT dbbackup.pg_dbrestore(
    'app',
    ARRAY['.../full.bak', '.../diff.bak',
          '.../1200.log.bak', '.../1300.log.bak']::text[],
    target_db := 'app_restored');
```

The restore writes to `_pg_dbbackup_restore_<16hex>` first, then drops
the target (if it exists) and renames the temp DB on top of it. Failures
roll back by dropping the temp DB.

### 6. Inspect a `.bak`

```sql
-- Header (single row)
SELECT * FROM dbbackup.pg_dbbackup_header('/var/backups/app-...-full.bak');

-- File list (one row per DATA entry)
SELECT * FROM dbbackup.pg_dbbackup_filelist('/var/backups/app-...-full.bak', 's3cret');

-- Verify magic bytes + SHA-256 footer
SELECT * FROM dbbackup.pg_dbbackup_verify('/var/backups/app-...-full.bak');
```

## Feature matrix

| Capability                       | SIMPLE                       | FULL                                  |
|----------------------------------|------------------------------|---------------------------------------|
| Backup payload                   | DDL + `COPY` binary          | Database file images + filtered WAL   |
| `FULL` backups                   | Yes                          | Yes                                   |
| `DIFFERENTIAL` (cumulative)      | Yes (re-dumps changed tables)| Yes (mtime-changed files + WAL range) |
| `LOG`                            | No                           | Yes (WAL only)                        |
| Point-in-time restore (`stop_at`)| No                           | Limited (scaffolded, see below)       |
| Cross-PG-version restore         | Yes                          | No                                    |
| zstd compression                 | Yes                          | Yes                                   |
| AES-256-GCM encryption           | Yes                          | Yes                                   |
| Online (no exclusive locks)      | Yes                          | Yes (`do_pg_backup_start/stop`)       |
| Per-database isolation           | Yes                          | Yes (WAL filtered by `dbOid`)         |
| Replaces existing target DB      | Yes (FORCE-drop + RENAME)    | Yes (FORCE-drop + RENAME)             |

## Known limitations

- **No in-process WAL replay.** FULL-mode restore injects the captured
  file images and parses the WAL stream for structure / PITR cutoffs,
  but does **not** apply WAL records during restore. The pragmatic
  workaround is to `VACUUM FREEZE; CHECKPOINT` the source database
  before a FULL-mode backup; this is what the round-trip tests do.
  See the header comment of `src/wal_replay.c` for the three viable
  paths to real replay.
- **Subprocess PITR scaffolding present, blocked by cluster-vs-DB scope
  mismatch.** A subprocess-recovery path (`src/subprocess_recovery.c`)
  drives `initdb` + `pg_ctl` against a synthetic PGDATA built from the
  FULL backup's files plus captured WAL segments
  (`BAKSECTION_WAL_SEGMENTS`). The synthetic cluster starts and begins
  archive recovery, but recovery fails with *"WAL file is from
  different database system"* because PG's redo is cluster-scoped: WAL
  segments carry the source cluster's sysid while initdb assigned a
  fresh sysid. Injecting the source's `pg_control` resolves the sysid
  but then the synthetic `pg_database` (a shared catalog) has no row
  for the source's `db_oid`, so we cannot connect to the recovered DB
  to `pg_dump`. A complete fix needs one of: (a) capture the entire
  cluster's `global/` shared catalogs at backup time — eliminates the
  per-DB advantage, or (b) public SQL API for `CREATE DATABASE` with a
  chosen OID matching `src_dboid` — none in PG17. The infrastructure
  is in place for future work; today's PITR remains partial.
- **PITR (`stop_at`) is partial.** The chain is scanned for the cutoff
  commit, but because records aren't applied, restored state reflects
  the chain-end on-disk image rather than the pre-cutoff state. A
  `stop_at` past every commit succeeds; one inside the window emits a
  `NOTICE` but cannot rewind.
- **FULL-mode DIFFERENTIAL uses mtime-based change detection.** It is
  conservative (no false negatives), not block-level. PG17's
  `pg_wal_summary` infrastructure is a future enhancement.
- **Inject step skips `global/` and tablespaces.** Per-DB restore only
  rewrites `base/<dboid>/...`. Cluster-wide files
  (`pg_filenode.map`, tablespaces) are captured in the `.bak` but not
  re-injected on restore.
- **TimescaleDB SIMPLE round-trip is not supported.** Hypertable chunks
  live as plain tables but their `_timescaledb_catalog` metadata is
  extension-owned and re-initialised empty on `CREATE EXTENSION`
  during restore. Backup succeeds; restore leaves orphaned chunk
  tables. Reconciliation is out of scope for v1.
- **Foreground only.** Backups run inline in the calling backend.
  There is no background-worker variant. Plan accordingly for very
  large databases.
- **Sections are buffered in memory.** Per-section streaming compression
  and encryption work, but the section payload is built in a
  `StringInfo` before flush. A future streaming variant would lift
  the practical size ceiling.

## Tests

The test suite runs against a Postgres 17 container that builds and
installs the extension from source.

```bash
cd tests/pg_dbbackup.Tests
dotnet run --project . -- --no-progress
```

Requirements:
- .NET 9 SDK
- Docker (the test fixture uses `docker cp` and Testcontainers)

The extension-compatibility tests
(`PgvectorTests`, `PgTextsearchTests`, `TimescaleDbTests`,
`MultiExtensionTests`) use a different container image that ships
those extensions pre-installed. They are skipped automatically when
the required extension is absent.
