# pg_dbbackup Progress Report

Status: per-database logical PITR v1 is implemented and test-covered.

The normal FULL-mode path no longer uses PostgreSQL native crash recovery,
raw WAL segments, `global/`, SLRU directories, or `pg_control`. FULL backups
are logical base snapshots. DIFFERENTIAL and LOG backups are database-scoped
logical stream backups read from a logical replication slot using the
`pg_dbbackup` output plugin.

## Implemented

- `.bak` container with metadata, schema, data, logical stream sections,
  compression, encryption, footer checksum, header/filelist/verify helpers.
- Cached Docker test images that build the extension once per source hash.
- `dbbackup.logical_chains` catalog and deterministic logical slot naming:
  `_pg_dbbackup_<dboid>`.
- FULL-mode chain slots and restore continuation slots are created as
  failover-enabled logical slots so PostgreSQL 17+ slot synchronization can
  carry the backup chain across primary promotion.
- Failover-slot status/readiness APIs:
  `pg_dbbackup_failover_slot_status`,
  `pg_dbbackup_failover_slot_ready`, and
  `pg_dbbackup_wait_failover_slot_ready`.
- FULL-mode base backup using the SIMPLE schema/data writer with FULL headers
  and logical-slot LSN anchoring.
- DIFFERENTIAL and LOG backups using the same logical stream format.
- LOG/DIFFERENTIAL stream consumption with `pg_logical_slot_get_changes`; FULL
  chain-slot anchoring uses `pg_replication_slot_advance` to fast-forward the
  new slot to the base snapshot boundary.
- Logical output plugin for `INSERT`, `UPDATE`, `DELETE`, and `TRUNCATE`.
- DDL journaling through event triggers into `dbbackup.ddl_log`; the output
  plugin decodes those rows as ordered SQL frames.
- Sequence journaling through transaction-end snapshots into
  `dbbackup.sequence_log`, replayed with `setval` at or before `stop_at`.
- Large-object FULL snapshots and transaction-end LOG frames for create,
  write/truncate-equivalent snapshot, and unlink behavior.
- Restore-side role handling that warns and creates NOLOGIN placeholder roles
  instead of failing when referenced owners/grantees are missing.
- TimescaleDB adapter support for hypertables, chunk DML mapped back to the
  user hypertable, multidimensional range/hash dimensions, continuous
  aggregates, retention policies, compression policies, and continuous
  aggregate refresh policies.
- Transaction replay that applies only complete committed transactions and
  stops before the first commit after `stop_at`.
- Restore-time trigger suppression for FULL data load and LOG replay, so user
  triggers are restored but do not double-fire while historical side effects
  are replayed.
- Transaction-end journals skip recovery/read-only transactions, so standbys
  can serve normal read sessions even when `pg_dbbackup` is preloaded and a
  FULL backup chain exists on the primary.
- Logical restore batching: one libpq round trip per committed transaction.
- FULL-mode feature validator that rejects unsupported physical/external
  features before writing a misleading `.bak` or advancing the logical chain.
- Early logical chain validation for DB name, backup type, differential
  placement, and LSN continuity.
- LOG/DIFFERENTIAL chain validation refuses to continue from a missing,
  temporary, invalidated, or non-failover `_pg_dbbackup_<dboid>` slot.
- Cross-container restore support: a FULL+LOG chain can be copied to a fresh
  PostgreSQL installation and restored there.
- Schema generator coverage for partitioned tables, identity/generated
  columns, domain constraints, functions/procedures, views, rewrite rules,
  triggers, and RLS policies.
- Historical physical WAL filtering, WAL replay, and subprocess native
  recovery code removed from the v1 build/source path.
- Memory-lifetime fixes around SPI and compressed section buffers so backend
  contexts are not used after `SPI_finish()`.

## Verified

- Full suite passes: 117 total, 117 passed, 0 skipped.
- Focused PostgreSQL feature matrix passes: 14/14.
- `TimescaleDbTests`: 9/9 passed, including FULL+LOG restore, PITR stop,
  multidimensional hypertables, and Timescale policy replay.
- `FullAdvancedCoverageTests`: 10/10 passed, including multi-LOG replay,
  restore branch chains, indexes, roles/ownership/grants, extension metadata,
  large table coverage, DDL replay, sequence PITR, and large-object replay.
- `CrossContainerRestoreTests`: passed against a second PostgreSQL container.
- `FailoverSlotTests`: passed against a real primary/standby pair; a
  synced-but-temporary standby slot is reported as not ready and LOG backup
  refuses to continue after promotion because PostgreSQL drops the temporary
  slot.
- `PgdogTests`: passed against a real primary/standby pair plus PgDog;
  backup succeeds through the primary route, direct standby backup rejects, and
  a PgDog standby route rejects instead of rerouting silently.
- `SimpleDifferentialTests`: 5/5 passed after the SPI lifetime fix.
- Extension groups pass for pg_textsearch, pgvector, TimescaleDB, and combined
  extension scenarios.
- Cached full-suite run completes in about two and a half minutes on this
  machine after image cache warmup, including the primary/standby failover-slot
  and PgDog routing tests.

## Current Constraints

- FULL-mode UPDATE/DELETE requires each user table to have a primary key or
  `REPLICA IDENTITY FULL`.
- FULL-mode backup requires `pg_dbbackup` in `shared_preload_libraries` so the
  transaction-end sequence and large-object journal runs in writer backends.
- `wal_level = logical` and sufficient replication slots/WAL retention are
  required for FULL-mode chains.
- Seamless continuation after primary promotion requires PostgreSQL logical
  slot synchronization to persist the failover slot on the standby before
  failover; `pg_dbbackup_failover_slot_ready('<db>')` must return true on the
  candidate standby.
- zstd-compressed and AES-encrypted sections still use whole-section
  processing. Plain sections stream directly to disk.
- Unsupported physical/external behavior is rejected before FULL/DIFF/LOG
  backup. The v1 list is tracked in `SUPPORT_MATRIX.md`.

## Next Work

1. Expand TimescaleDB coverage as new features are found: columnstore-era
   compression behavior, more policy permutations, distributed/multinode cases
   if supported by the installed TimescaleDB version.
2. Add performance tier 2: streaming zstd/AES framing, checksum without a
   full-file reread, parallel table load, and optional parallel index rebuild.
3. Add stress tests for very large logical streams, many relations, many
   sequences, many large objects, and high non-target database write volume.
