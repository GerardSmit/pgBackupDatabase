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
(pgvector, pg_textsearch). The FULL recovery model is now per-database
logical PITR: a FULL backup stores schema plus binary `COPY` table data,
and DIFFERENTIAL/LOG backups store a database-scoped logical decoding
stream produced by the `pg_dbbackup` output plugin. Normal FULL-mode
backups do not include raw WAL segments, `global/`, SLRU directories, or
`pg_control`. DDL, sequence state, large objects, and TimescaleDB adapter
state are represented logically in the v1 backup chain.

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

`pg_dbbackup` FULL mode requires:

- `shared_preload_libraries = 'pg_dbbackup'` (plus any extension preload
  libraries such as `timescaledb`)
- `wal_level = logical`
- enough `max_replication_slots` for the databases/chains being backed up
- enough WAL retention for the logical slots to remain healthy

FULL-mode chain slots are created as PostgreSQL failover logical slots. In an
HA cluster, connect backup/restore jobs directly to the current primary. To
continue a LOG/DIFFERENTIAL chain after promotion, configure PostgreSQL 17+
logical slot synchronization for the standby: a physical replication slot,
`hot_standby_feedback = on`, `sync_replication_slots = on` on the standby, and
`synchronized_standby_slots` on the primary for the standby's physical slot.
Before planned promotion, run
`dbbackup.pg_dbbackup_failover_slot_ready('<db>')` on the candidate standby.
It must return `true`; a synced but temporary slot does not survive promotion.
If a promoted primary does not have a persistent synced `_pg_dbbackup_<dboid>`
slot, LOG/DIFFERENTIAL backup refuses to continue and you must take a new FULL
backup.

When using PgDog or another SQL proxy, backup jobs must be routed to the
primary endpoint. `pg_dbbackup` does not reroute a request that reaches a hot
standby; PostgreSQL rejects the write/slot work there. PgDog primary routing is
tested, and a PgDog route pointed at the standby is expected to fail instead of
silently forwarding the backup.

SIMPLE mode does not need logical decoding, but using the same preload setting
in test/dev clusters is fine.

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

### 3. Take a DIFFERENTIAL (logical stream since the FULL)

```sql
SELECT dbbackup.pg_dbbackup(
    'app',
    '/var/backups/app-2026-05-11-diff.bak',
    type          := 'differential',
    base_filepath := '/var/backups/app-2026-05-11-full.bak');
```

### 4. Take a LOG backup (FULL mode only; logical stream, no data files)

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
| Backup payload                   | DDL + `COPY` binary          | DDL + `COPY` base + logical streams   |
| `FULL` backups                   | Yes                          | Yes                                   |
| `DIFFERENTIAL` (cumulative)      | Yes (re-dumps changed tables)| Yes (logical stream since base)       |
| `LOG`                            | No                           | Yes (logical stream only)             |
| Point-in-time restore (`stop_at`)| No                           | Yes, via logical transaction replay   |
| Cross-PG-version restore         | Yes                          | Same supported PG majors tested       |
| zstd compression                 | Yes                          | Yes                                   |
| AES-256-GCM encryption           | Yes                          | Yes                                   |
| Online                           | Yes                          | Yes; FULL takes table `SHARE` locks   |
| Per-database isolation           | Yes                          | Yes; logical slots are DB-scoped      |
| Replaces existing target DB      | Yes (FORCE-drop + RENAME)    | Yes (FORCE-drop + RENAME)             |

## Known limitations

- **FULL-mode UPDATE/DELETE requires replica identity.** User tables
  must have a primary key or `REPLICA IDENTITY FULL`; otherwise FULL
  backup rejects the database before creating a misleading chain.
- **FULL mode requires preload.** `pg_dbbackup` must be present in
  `shared_preload_libraries` so transaction-end journals for sequences and
  large objects run in writer backends.
- **FULL-mode chain slots are failover-enabled.** PostgreSQL can synchronize
  those slots to standbys when the cluster is configured for logical slot
  synchronization. `pg_dbbackup_failover_slot_status`,
  `pg_dbbackup_failover_slot_ready`, and
  `pg_dbbackup_wait_failover_slot_ready` expose whether a candidate standby has
  a persistent synced slot. LOG/DIFFERENTIAL backup refuses to continue if the
  chain slot is missing, temporary, invalidated, or recreated without failover
  support.
- **PgDog/proxy backup routes must target primary.** Backup through a PgDog
  primary route is tested. A route that reaches a standby fails; the extension
  deliberately does not proxy the request to primary from inside PostgreSQL.
- **DDL is captured through event triggers.** The v1 path journals
  `ddl_command_end` and `sql_drop` command text and replays it in logical
  transaction order. Newly found DDL command families should get tests or an
  explicit rejection path.
- **Sequences are journaled.** FULL captures sequence state and LOG replay
  applies the latest `setval` at or before `stop_at`.
- **Large objects are supported.** FULL captures large-object metadata/pages;
  LOG captures changed snapshots and unlink behavior.
- **TimescaleDB coverage includes adapter replay.** Hypertables,
  multidimensional dimensions, chunk DML through the root hypertable,
  continuous aggregates, retention policies, compression policies, refresh
  policies, extension version preservation, chunk creation during LOG replay,
  and `stop_at` filtering are tested.
- **Foreground only.** Backups run inline in the calling backend.
  There is no background-worker variant. Plan accordingly for very
  large databases.
- **Compressed/encrypted sections are buffered in memory.** Plain sections
  stream directly to disk, and FULL restore streams DATA entries into
  `COPY FROM` in chunks. zstd/AES sections are still processed as whole
  section blobs, and the footer checksum currently rereads the finished
  `.bak`.

## Tests

The test suite builds cached Docker images that already contain the
compiled extension, then starts containers from those images. The first
run after source changes rebuilds the final Docker layer; repeated runs
reuse the image and avoid `apt-get`/`make` during fixture startup.

```bash
cd tests/pg_dbbackup.Tests
dotnet run --project . -- --no-progress
```

Requirements:
- .NET SDK
- Docker with BuildKit-compatible `docker build`

The extension-compatibility tests
(`PgvectorTests`, `PgTextsearchTests`, `TimescaleDbTests`,
`MultiExtensionTests`) use a different container image that ships
those extensions pre-installed. They are skipped automatically when
the required extension is absent.
