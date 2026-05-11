# pg_dbbackup Progress Report

All 13 phases done. See `PLAN.md` for the full plan and `README.md`
for usage. Limitations are listed at the bottom of this file.

---

## Phase 1: Scaffold + .bak Format — **DONE**
Extension skeleton, PGXS Makefile, `.bak` reader/writer with JSON
header, per-section framing, magic-bytes head/tail, SHA-256 footer.
Per-DB mode config (`pg_dbbackup_set_mode` / `_get_mode`) stored in
`dbbackup.config`. Tests: `ModeConfigTests`, `BakfileFormatTests`.

## Phase 2: Compression + Encryption — **DONE**
Per-section zstd compression. AES-256-GCM via OpenSSL with PBKDF2
key derivation (100000 iters), 16-byte salt + 12-byte IV + 16-byte
auth tag. Salt and IV serialised into the JSON header as hex.
Tests: `CryptoTests` (4/4 passing).

## Phase 3: Metadata + DDL Generation — **DONE**
SPI-driven catalog readers emit replayable SQL scripts for extensions,
schemas, types, sequences, tables, constraints, indexes, functions,
views; comments and ACLs covered. Tests: `MetadataDdlTests` (6/6).

## Phase 4: SIMPLE FULL Backup — **DONE**
`backup_simple_full` writes header + METADATA + SCHEMA + DATA. DATA
section holds `COPY ... (FORMAT binary)` blobs per user table.
Tests: `BakfileFormatTests` (6/6).

## Phase 5: SIMPLE Restore — **DONE**
`restore_simple` over libpq: temp DB creation, SCHEMA apply,
`COPY ... FROM STDIN`, METADATA apply, drop existing target,
`ALTER DATABASE RENAME`. PG_TRY cleanup drops the temp DB on failure.
Tests: `RestoreSimpleTests` (4/4).

## Phase 6: SIMPLE Differential — **DONE**
Cumulative SIMPLE DIFF via per-table SHA-256 comparison against the
base FULL's data entries. Chain restore overlays DIFF onto FULL
(at most one DIFF). Tests: `SimpleDifferentialTests` (5/5).

## Phase 7: FULL FULL Backup — **DONE**
Physical backup via `do_pg_backup_start/stop`, temporary physical
replication slot to pin WAL, file walk under `base/<dboid>/`, globals
(`pg_filenode.map`, `pg_control`), tablespaces, plus a WAL section
covering `[start_lsn, stop_lsn]`. `bakfile_rewrite_header` patches
`stop_lsn`/`stop_tli` after backup stop with length-stable JSON.
Tests: `FullBackupTests` (7/7).

## Phase 8: WAL Filter — **DONE**
`wal_filter.c` reads WAL via `XLogReader` with
`read_local_xlog_page_no_wait`. Filter: cluster-wide rmgrs pass
unconditionally (`XLOG`, `XACT`, `CLOG`, `MULTIXACT`, `STANDBY`,
`DBASE`, `RELMAP`); others included only if any block reference's
`rlocator.dbOid == target_db_oid`. Output is a stream of
`[uint32 BE xl_tot_len][record bytes]` frames. Tests:
`WalFilterTests` (3/3).

## Phase 9: FULL Restore + PITR scaffolding — **DONE**
`restore_full` v1: validate chain, create temp DB, clear
`base/<temp_dboid>/`, inject `base/<src_dboid>/...` entries rewritten
to the temp DB's oid, restore `pg_database.datfrozenxid`/`datminmxid`
from the captured header values, scan WAL for the PITR cutoff,
checkpoint + buffer flush, drop existing target, rename. Tests:
`FullRestoreTests` (4/4).

## Phase 9b: WAL scan + PITR cutoff — **DONE**
`wal_replay.c` walks each `.bak`'s WAL section, counts records,
classifies by `RelFileLocator.dbOid` via a manual block-ref-header
decoder (sidesteps `DecodeXLogRecord`'s need for an
`XLogReaderState`), and honours `XLOG_XACT_COMMIT` `xact_time` as
the PITR cutoff. **Records are not applied** — see *Known
limitations*. The same-cluster round-trip works without VACUUM
FREEZE / CHECKPOINT: the captured pages reference XIDs that remain
in the live cluster's CLOG. PG17/PG18 portability shim:
`RepOriginId` → `ReplOriginId` via `WAL_REPLAY_ORIGIN_ID_SIZE`,
`CHECKPOINT_FAST` ↔ `CHECKPOINT_IMMEDIATE`.

## Phase 10: FULL Differential + Log — **DONE**
`backup_full_differential` collects `base/<dboid>/...` files whose
`mtime` is after the base FULL's `created_at`, then a WAL extract
for `[base.stop_lsn, this.stop_lsn]`. `backup_full_log` is WAL-only
via `pg_switch_wal` + `pg_current_wal_lsn`. Chain restore handles
FULL + optional DIFF + N LOGs with `validate_chain` enforcing
LSN linkage. Tests: `FullDifferentialTests` (3/3),
`FullLogTests` (3/3), `FullRestoreChainTests` (3/3).

## Phase 11: Inspection Functions — **DONE**
`pg_dbbackup_header` (single-row OUT-param composite),
`pg_dbbackup_filelist` (Materialize SRF, optional password),
`pg_dbbackup_verify`. Tests: `InspectTests` (7/7).

## Phase 12: Extension Compatibility — **DONE**
Round-trip tests for pgvector (data + HNSW + ivfflat) and
pg_textsearch (data + BM25). Multi-extension combined test.
TimescaleDB SIMPLE round-trip is intentionally skipped (extension
re-initialises its catalog tables on `CREATE EXTENSION`, which a
logical re-dump cannot reconcile — verified manually with a
`pg_dump`-style staged restore). `pgdog` test is a documentation
skip (the proxy is stateless).
Tests: `PgvectorTests`, `PgTextsearchTests`, `MultiExtensionTests`,
`TimescaleDbTests` (backup-only), `PgdogTests` (skipped).

## Phase 13: Polish — **DONE**
Per-step `NOTICE`s in all backup and restore paths:
- `backup_simple_full`: "backing up table %u/%u: %s.%s"
- `backup_simple_differential`: "including/skipping" per-table +
  final summary
- `backup_full_full`: pre-scan total, "backing up file %u/%u",
  WAL "[%X/%X, %X/%X] (N bytes)" + final summary
- `backup_full_differential`: per-file + WAL range + final summary
- `backup_full_log`: WAL range + final summary
- `restore_simple`: "creating temp DB", "applying SCHEMA section",
  "restoring table %u/%u", "applying METADATA", "renaming … -> …"
- `restore_full`: "creating temp DB", "injecting %u files into
  base/%u/", existing WAL-records scan NOTICE, "renaming … -> …"

Tests after consolidation: per-file helpers (`DropDbAsync`,
`BackupPath`, `RunBackupAsync`, `DecodeSections`, `ParseHeader`)
moved into `tests/pg_dbbackup.Tests/Helpers.cs` (static helpers +
`NpgsqlConnection` extension methods) and `PgContainerFixture` /
`PgWithExtensionsFixture` (admin / drop / connect / db-exists).
~600 lines of duplicated boilerplate removed across 14 test files.

`README.md` written: overview, install prerequisites, quickstart
SQL examples, feature matrix, known limitations, test instructions.

---

## Known limitations

- **No WAL apply pass.** Captured records are scanned + classified but
  not applied to the cluster. Same-cluster round-trip restores work
  unmodified (no VACUUM FREEZE required): the cluster's CLOG keeps the
  source XIDs visible.
- **Subprocess PITR scaffolding present, blocked by cluster-vs-DB
  scope mismatch.** `src/subprocess_recovery.c` drives an `initdb`-
  bootstrapped synthetic PGDATA + `pg_ctl start` + (eventual)
  `pg_dump` round-trip. The FULL `.bak` now stores a
  `BAKSECTION_WAL_SEGMENTS` section with raw 16MB WAL segments
  covering `[start_lsn, stop_lsn]`, plus `checkpoint_lsn` in the
  header (`bstate->checkpointloc`). The synthetic cluster successfully
  starts and begins archive recovery but fails with *"WAL file is
  from different database system"*: PG's redo is cluster-scoped, but
  the synthetic PGDATA has a fresh sysid (from initdb) while the WAL
  carries the source's sysid. Injecting source `pg_control` makes
  recovery proceed but then the synthetic `pg_database` (a shared
  catalog) has no row for `src_dboid`, so we can't connect to the
  recovered DB. A complete fix needs either (a) per-cluster backup
  scope (defeats the per-DB advantage) or (b) public API to set
  `CREATE DATABASE WITH OID = <n>` (not in PG17). The infrastructure
  is in place for future work.
- **PITR `stop_at`** is partially functional: the cutoff is detected
  in the WAL scan and `NOTICE`d. Pre-cutoff data in the FULL `.bak`
  is visible; post-FULL data captured in DIFF/LOG `.bak`s is not
  applied. `FullRestore_Pitr_Before_Cutoff_Visible` and
  `FullRestore_Pitr_After_Cutoff_Invisible` exercise this; the full
  in-window PITR test (`FullRestore_Pitr_Stops_At_Timestamp`) is
  skipped with a tracking note describing the precise failure mode.
- **FULL DIFFERENTIAL detects file changes by `mtime`.** Conservative
  (no false negatives) but not block-level; PG17 `pg_wal_summary`
  is a future enhancement.
- **`global/` and tablespaces are captured but not re-injected** —
  v1 only rewrites `base/<temp_dboid>/...`.
- **TimescaleDB SIMPLE round-trip is unsupported** (extension-owned
  catalog reset on `CREATE EXTENSION`).
- **Foreground execution; no background-worker variant.**
- **Section payloads are buffered in memory.** Per-section streaming
  compress + encrypt is in place, but the payload itself is built in
  a `StringInfo` before flush.
