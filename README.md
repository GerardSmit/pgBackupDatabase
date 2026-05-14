# pg_dbbackup

[![CI](https://github.com/GerardSmit/pgBackupDatabase/actions/workflows/ci.yml/badge.svg)](https://github.com/GerardSmit/pgBackupDatabase/actions/workflows/ci.yml)

Per-database online backup and restore for PostgreSQL, modelled on SQL Server's
`BACKUP DATABASE` / `BACKUP LOG` workflow. Each backup is a self-contained
`.bak` file (or S3 object) with a JSON header, optional zstd compression, and
optional AES-256-GCM encryption.

- `FULL`, `DIFFERENTIAL`, and `LOG` backup types per database
- Two recovery models: `SIMPLE` (logical dump) and `FULL` (logical decoding + DDL journal) with point-in-time restore
- Self-contained `.bak` files with magic bytes, JSON header, and SHA-256 footer
- zstd compression + AES-256-GCM encryption
- Local filesystem **or** S3 / S3-compatible storage (MinIO, R2, B2, Wasabi)
- Background-worker scheduler and async backups (`pg_dbbackup_async`)
- HA-ready: failover logical slots + standby calls auto-routed to primary
- Atomic restore: writes to temp DB, then renames over the target

🚀 **Status**: v0.0.1. `SIMPLE` is feature-complete. `FULL` covers per-database
logical PITR. See [`docs/SUPPORT_MATRIX.md`](docs/SUPPORT_MATRIX.md) for the
detailed feature matrix.

## PostgreSQL Version Compatibility

`pg_dbbackup` supports PostgreSQL **17 and 18**. CI tests both on Ubuntu.

## Installation

### Pre-built Binaries

Download release tarballs from the
[Releases page](https://github.com/GerardSmit/pgBackupDatabase/releases).
Tarballs are Linux amd64 and named
`pg_dbbackup-<version>-pg<pg-major>-linux-amd64.tar.gz`.

```sh
curl -fsSL -o pg_dbbackup.tar.gz \
  https://github.com/GerardSmit/pgBackupDatabase/releases/latest/download/pg_dbbackup-0.0.1-pg17-linux-amd64.tar.gz
sudo tar -xzf pg_dbbackup.tar.gz -C /
```

### Build from Source

System dependencies (Debian/Ubuntu):

```sh
sudo apt install \
  postgresql-server-dev-17 \
  libzstd-dev libssl-dev libcurl4-openssl-dev \
  build-essential
```

Then:

```sh
git clone https://github.com/GerardSmit/pgBackupDatabase
cd pgBackupDatabase
make
sudo make install
```

If your machine has multiple Postgres installations, point `PG_CONFIG` at the
one you want:

```sh
export PG_CONFIG=/usr/lib/postgresql/18/bin/pg_config
make clean && make && sudo make install
```

## Cluster Configuration

`pg_dbbackup` must be preloaded and the cluster must run with logical WAL:

```
shared_preload_libraries = 'pg_dbbackup'   # add to existing list if needed
wal_level                = logical
```

Restart Postgres. Then, once per database:

```sql
CREATE EXTENSION pg_dbbackup;
```

The extension is superuser-only and non-relocatable — its schema is fixed to
`dbbackup`.

For LOG/DIFFERENTIAL chains: ensure `max_replication_slots` is high enough to
cover every backed-up database, and that WAL retention keeps logical slots
healthy. HA clusters need PostgreSQL 17+ logical slot synchronization
configured on the standby — see the [Disaster Recovery Runbook](#disaster-recovery-runbook).

## Environment Variables

S3 credentials are read from the **postgres backend process**, not from the
psql client session. Set them on the postgres service before starting the
server.

| Variable | Required | Purpose |
|---|---|---|
| `AWS_ACCESS_KEY_ID` | yes (for S3) | S3 SigV4 authentication |
| `AWS_SECRET_ACCESS_KEY` | yes (for S3) | S3 SigV4 authentication |
| `AWS_SESSION_TOKEN` | optional | STS / assumed-role credentials |
| `AWS_REGION` | optional | Endpoint resolution (defaults to `us-east-1` or the storage target's `region` column) |

### Docker Compose

```yaml
services:
  postgres:
    image: postgres:17
    environment:
      POSTGRES_PASSWORD: example
      AWS_ACCESS_KEY_ID: AKIA...
      AWS_SECRET_ACCESS_KEY: ...
      AWS_REGION: eu-west-1
    command: >
      postgres
      -c shared_preload_libraries=pg_dbbackup
      -c wal_level=logical
```

Local-file backups (the next section) do not need any of these variables.

## Getting Started

`SIMPLE` is the default recovery model. Switch a database to `FULL` if you need
LOG backups / PITR:

```sql
SELECT dbbackup.pg_dbbackup_set_mode('app', 'full');
SELECT dbbackup.pg_dbbackup_get_mode('app');   -- 'full'
```

All `pg_dbbackup` functions live in the `dbbackup` schema.

## Local File Backups

`dbbackup.pg_dbbackup()` writes a single `.bak` file to a path on the
**server**'s filesystem. The backend process writes the file directly.

### Take a FULL backup

```sql
SELECT dbbackup.pg_dbbackup(
    'app',                                      -- target database
    '/var/backups/app-2026-05-11-full.bak',     -- server-side path
    type     := 'full',
    compress := true,
    password := 's3cret');                      -- optional AES-256-GCM
```

### Take a DIFFERENTIAL (logical stream since the FULL)

```sql
SELECT dbbackup.pg_dbbackup(
    'app',
    '/var/backups/app-2026-05-11-diff.bak',
    type          := 'differential',
    base_filepath := '/var/backups/app-2026-05-11-full.bak');
```

### Take a LOG backup (FULL mode only)

```sql
SELECT dbbackup.pg_dbbackup(
    'app',
    '/var/backups/app-2026-05-11-1200.log.bak',
    type          := 'log',
    base_filepath := '/var/backups/app-2026-05-11-diff.bak');
```

### Restore a chain

```sql
-- SIMPLE: FULL + at most one DIFFERENTIAL
-- FULL  : FULL + optional DIFFERENTIAL + zero or more LOGs
SELECT dbbackup.pg_dbrestore(
    ARRAY['/var/backups/app-...-full.bak',
          '/var/backups/app-...-diff.bak',
          '/var/backups/app-...-1200.log.bak']::text[],
    target_db := 'app_restored',
    password  := 's3cret');
```

The restore writes to `_pg_dbbackup_restore_<16hex>` first, then drops the
target (if it exists) and renames the temp DB on top. Failures roll back by
dropping the temp DB.

Note: `pg_dbbackup` (local-file output) is rejected on hot standbys because the
`.bak` would land on the primary's filesystem. Use `pg_dbbackup_to_storage`
instead — see below.

## S3 / Storage-Target Backups

Off-box backups use a named **storage target** (S3, S3-compatible, or local
filesystem prefix) and an optional **backup set** that groups databases.

### 1. Create an S3 target

```sql
SELECT dbbackup.create_s3_target(
    name         := 'prod_s3',
    bucket       := 'db-backups',
    prefix       := 'postgres/prod',
    region       := 'eu-west-1',
    endpoint_url := NULL,             -- set for S3-compatible providers
    encryption   := 'sse-s3');        -- 'none' | 'sse-s3' | 'sse-kms'
```

Credentials come from environment variables (see above). The target stores only
non-secret configuration.

### 2. Group databases into a backup set

```sql
SELECT dbbackup.create_backup_set('nightly', storage_target := 'prod_s3');
SELECT dbbackup.add_database_to_backup_set('nightly', 'app');
```

### 3. Run a backup

```sql
SELECT dbbackup.pg_dbbackup_to_storage(
    'app',
    type       := 'full',
    backup_set := 'nightly',
    compress   := true,
    password   := 's3cret');
```

The function returns a `uuid` (the `backup_id`). Subsequent `differential` /
`log` backups against the same set chain automatically.

### 4. Restore from storage

```sql
-- Latest available chain
SELECT dbbackup.pg_dbrestore_from_storage(
    dbname         := 'app',
    storage_target := 'prod_s3',
    target_db      := 'app_restored',
    password       := 's3cret');

-- Point-in-time restore
SELECT dbbackup.pg_dbrestore_at(
    dbname         := 'app',
    target_db      := 'app_restored',
    stop_at        := '2026-05-11 12:30:00+00',
    storage_target := 'prod_s3',
    password       := 's3cret');
```

When called on a hot standby, `pg_dbbackup_to_storage` automatically reconnects
to the current primary using the standby's `primary_conninfo` and re-issues the
call there. This makes it safe to route the call through PgDog or another SQL
proxy.

## Inspecting Backups

```sql
-- Header (single row)
SELECT * FROM dbbackup.pg_dbbackup_header('/var/backups/app-...-full.bak');

-- File list (one row per DATA entry)
SELECT * FROM dbbackup.pg_dbbackup_filelist('/var/backups/app-...-full.bak', 's3cret');

-- Verify magic bytes + SHA-256 footer
SELECT * FROM dbbackup.pg_dbbackup_verify('/var/backups/app-...-full.bak');
```

## Feature Matrix

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

## High Availability

FULL-mode chain slots are PostgreSQL **failover logical slots**. With logical
slot synchronization configured (physical replication slot,
`hot_standby_feedback = on`, `sync_replication_slots = on` on the standby,
`synchronized_standby_slots` on the primary), a promoted standby can continue
the chain without a fresh FULL backup.

Before planned promotion:

```sql
SELECT dbbackup.pg_dbbackup_failover_slot_ready('app');
-- must return true; a synced-but-temporary slot does not survive promotion
```

If the slot is missing, temporary, invalidated, or non-failover after
promotion, LOG/DIFFERENTIAL backup refuses to continue and the next backup
must be a fresh FULL.

`pg_dbbackup_to_storage` calls issued on a hot standby auto-route to the
current primary via `primary_conninfo`. `pg_dbbackup` (local-file output) is
rejected on standbys — use the storage form instead.

## Disaster Recovery Runbook

These procedures cover cases that show up in practice. All commands assume
superuser, connected to the database whose chain is being repaired unless
noted otherwise.

### Inspecting chain state

`dbbackup.logical_chains` records the FULL-mode chain endpoint per database:

```sql
SELECT db_name, slot_name, confirmed_lsn, updated_at
FROM dbbackup.logical_chains
WHERE db_oid = (SELECT oid FROM pg_database WHERE datname = current_database());

SELECT slot_name, slot_type, plugin, confirmed_flush_lsn, restart_lsn,
       invalidation_reason
FROM pg_replication_slots
WHERE slot_name LIKE '\_pg\_dbbackup\_%';
```

`logical_chains.confirmed_lsn` must equal the `stop_lsn` of the most recent
`.bak` (see `dbbackup.pg_dbbackup_header(path)`) and must be **≤**
`pg_replication_slots.confirmed_flush_lsn` for that slot. The next backup
reconciles forward automatically; legacy state where the slot is ahead of the
chain requires a fresh FULL backup.

### A backup crashed mid-write

The writer streams into `<path>.bak.tmp` and renames atomically over the final
path on success. A crash mid-write therefore leaves an orphan `*.bak.tmp` —
never a torn `*.bak` — so restore can never read a partial artifact. The
scheduler background worker sweeps `/tmp/pg_dbbackup_*.bak{,.tmp}` files older
than one hour automatically; you can also delete them by hand.

Async jobs in `dbbackup.backup_jobs` whose background worker died are reaped
by the same scheduler tick: rows stuck in `pending`/`running` for more than
one hour are moved to `failed` with `error_msg` describing the reason. Poll
`dbbackup.pg_dbbackup_status(backup_id)` rather than relying on a hung row.

### `previous backup does not match the active logical PITR chain`

`base_filepath`'s `stop_lsn` differs from `logical_chains.confirmed_lsn`.
Either supply the latest `.bak` as the base, or — if intermediate `.bak`s are
lost — take a new FULL.

### `logical PITR slot "..." is invalidated`

The slot was invalidated by Postgres (typically `max_slot_wal_keep_size`).
Take a new FULL; the extension drops the invalidated slot and creates a fresh
one.

### A `.bak` is corrupted

`dbbackup.pg_dbbackup_verify(path)` checks magic bytes and the SHA-256 footer.
If verification fails, restore the chain up to the previous good `.bak` and
take a new FULL.

### A restore failed partway

Restores write to `_pg_dbbackup_restore_<16hex>` and only rename on success.
A failed restore leaves the target database untouched. Clean up stale temp
DBs manually if needed:

```sql
SELECT format('DROP DATABASE %I WITH (FORCE)', datname)
FROM pg_database
WHERE datname LIKE '\_pg\_dbbackup\_restore\_%';
```

### A slot is stuck after manual cleanup

If the slot is proven unused (chain row deleted, no `.bak` references it):

```sql
SELECT pg_drop_replication_slot('_pg_dbbackup_<oid>');
```

**Do not drop a slot whose `confirmed_flush_lsn` exceeds the last `.bak`'s
`stop_lsn`** — WAL behind that LSN will be reclaimed.

## Limitations

- **FULL-mode UPDATE/DELETE requires replica identity.** User tables must have
  a primary key or `REPLICA IDENTITY FULL`; otherwise FULL backup rejects the
  database before creating a misleading chain.
- **Foreground only.** Backups run inline in the calling backend; use
  `pg_dbbackup_async` for fire-and-forget invocation against the scheduler
  worker.
- **Compressed/encrypted sections are buffered in memory.** Plain sections
  stream directly to disk, and FULL restore streams DATA entries into
  `COPY FROM` in chunks. zstd/AES sections are still processed as whole
  section blobs.
- **DDL is captured through event triggers.** The v1 path journals
  `ddl_command_end` and `sql_drop` command text. New DDL command families
  need tests or an explicit rejection path.
- See [`docs/SUPPORT_MATRIX.md`](docs/SUPPORT_MATRIX.md) for the full list of
  rejected features (unlogged tables, foreign tables, materialized views,
  publications/subscriptions, custom range types, etc.).

## Tests

The test suite builds cached Docker images that already contain the compiled
extension, then starts containers from those images. The first run after
source changes rebuilds the final Docker layer; repeated runs reuse the image
and avoid `apt-get`/`make` during fixture startup.

```sh
cd tests/pg_dbbackup.Tests
dotnet test
```

Requirements:

- .NET 10 SDK
- Docker with BuildKit-compatible `docker build`

The extension-compatibility tests (`PgvectorTests`, `PgTextsearchTests`,
`TimescaleDbTests`, `MultiExtensionTests`) use a different container image
that ships those extensions pre-installed. They are skipped automatically when
the required extension is absent.

## License

MIT — see [LICENSE](LICENSE).

## Contributing

- **Bug Reports**: [Create an issue](https://github.com/GerardSmit/pgBackupDatabase/issues)
- **Pull Requests**: PRs welcome against `main`
