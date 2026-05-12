# pg_dbbackup

<!-- TODO: replace OWNER/REPO with the actual GitHub slug once the repo is pushed -->
[![CI](https://github.com/OWNER/REPO/actions/workflows/ci.yml/badge.svg)](https://github.com/OWNER/REPO/actions/workflows/ci.yml)

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
`pg_dbbackup.control` plus `sql/pg_dbbackup--0.0.1.sql` into
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

When `dbbackup.pg_dbbackup_to_storage()` is called on a hot standby, the
extension automatically re-issues the call on the primary using PostgreSQL's
own `primary_conninfo` GUC. The standby opens a libpq connection to the
primary, sets `dbbackup.in_remote_invocation = on` on it as a recursion guard,
and forwards the call verbatim. The backup itself — slot work, journal reads,
S3 upload — runs on the primary; the standby returns the routed `backup_id`.

For this to work, `primary_conninfo` must be set on the standby (the usual
streaming-replication setup already satisfies this) and the credentials it
carries must be accepted by the primary's `pg_hba.conf`. If `primary_conninfo`
is empty, the call fails with `object_not_in_prerequisite_state` and a hint
pointing at the GUC.

`dbbackup.pg_dbbackup()` (local-file output) is **rejected** on a standby with
`read_only_sql_transaction` because the `.bak` would land on the primary's
filesystem, not the caller's. Use `pg_dbbackup_to_storage()` so the artifact
ends up in shared storage where the location is unambiguous.

With PgDog or another SQL proxy: a standby-routed call to
`pg_dbbackup_to_storage()` now succeeds (auto-routed by the extension); a
standby-routed call to `pg_dbbackup()` still fails — by design.

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
- **Standby calls auto-route to the primary.** When invoked on a hot standby,
  `pg_dbbackup_to_storage` opens a libpq connection to the primary using
  `primary_conninfo` and re-issues the call there with the recursion-guard GUC
  `dbbackup.in_remote_invocation = on`. `pg_dbbackup` (local-file output) is
  rejected on a standby because the artifact would land on the primary's
  filesystem. PgDog/proxy primary and standby routes for the storage entry are
  both tested.
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

## Disaster recovery runbook

These procedures cover the cases that show up in practice. All commands
assume superuser, connected to the database whose chain is being
repaired unless noted otherwise.

### Inspecting chain state

`dbbackup.logical_chains` records the FULL-mode chain endpoint per
database:

```sql
SELECT db_name, slot_name, confirmed_lsn, updated_at
FROM dbbackup.logical_chains
WHERE db_oid = (SELECT oid FROM pg_database WHERE datname = current_database());

SELECT slot_name, slot_type, plugin, confirmed_flush_lsn, restart_lsn,
       invalidation_reason
FROM pg_replication_slots
WHERE slot_name LIKE '\_pg\_dbbackup\_%';
```

`logical_chains.confirmed_lsn` must equal the `stop_lsn` of the most
recent `.bak` in the chain (see `dbbackup.pg_dbbackup_header(path)`)
and must be **≤** `pg_replication_slots.confirmed_flush_lsn` for that
slot. The next DIFFERENTIAL/LOG backup reconciles slot.confirmed_flush_lsn
forward to logical_chains.confirmed_lsn automatically; legacy state where
the slot is ahead of the chain (an upgrade artifact) requires manual
intervention — take a fresh FULL backup.

### A backup crashed mid-write

If the backend died while writing a `.bak` and the file is partial,
delete the partial file. The chain row is only updated on success, so
the next DIFFERENTIAL/LOG against the previous good `.bak` will pick
up where it left off.

### `previous backup does not match the active logical PITR chain`

Raised when `base_filepath`'s `stop_lsn` differs from
`logical_chains.confirmed_lsn`. Either the supplied base is older than
the most recent successful chain entry (use the latest `.bak` instead),
or someone has hand-edited `logical_chains`. If you have lost the
intermediate `.bak` files, take a new FULL backup — there is no way to
synthesize the gap from WAL alone.

### `logical PITR slot "..." is invalidated`

The replication slot was invalidated by Postgres (typically because of
`max_slot_wal_keep_size`). LOG/DIFFERENTIAL backups cannot continue.
Take a new FULL backup; the extension will drop the invalidated slot
and create a fresh one.

### A `.bak` is corrupted

`dbbackup.pg_dbbackup_verify(path)` checks magic bytes and the SHA-256
footer. If verification fails, the file is unusable; restore the chain
up to the previous good `.bak` and take a new FULL.

### A restore failed partway

The restore writes to `_pg_dbbackup_restore_<16hex>` and only renames
over the target on success. If the restore aborted, the target
database is untouched. Stale temp DBs are dropped automatically on the
next restore attempt; to clean them up manually:

```sql
SELECT format('DROP DATABASE %I WITH (FORCE)', datname)
FROM pg_database
WHERE datname LIKE '\_pg\_dbbackup\_restore\_%';
```

### A slot is stuck after manual cleanup

If you have proven the slot is no longer needed (e.g., the chain row
was deleted and you have no `.bak` that references it),
`SELECT pg_drop_replication_slot('_pg_dbbackup_<oid>');` removes it.
**Do not drop a slot whose `confirmed_flush_lsn` exceeds the last
`.bak`'s `stop_lsn`** — WAL behind that LSN will be reclaimed and any
in-flight backup attempt will fail with a chain-LSN-gap error.

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
