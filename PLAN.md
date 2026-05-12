# pg_dbbackup: Per-Database Logical PITR Plan

## Goal

Build PostgreSQL backups that are genuinely per-database:

- A `.bak` for one database must not contain raw WAL from other databases.
- A `.bak` for one database must not contain `global/`, SLRU directories,
  `pg_control`, or other cluster recovery state.
- Point-in-time restore must stop on transaction boundaries and restore the
  requested database only.
- Extension support must be tested explicitly. Unsupported features must fail
  loudly before backup/restore, not produce a misleading backup.

The native PostgreSQL crash-recovery path is cluster-scoped. It is not the
primary design for this extension. Native recovery can remain only as a
separate experimental/diagnostic mode; it must not be used for the per-database
PITR product path.

---

## Core Decision

Use a per-database logical change log for PITR.

The SQL Server-style names remain:

| Mode | Meaning |
|------|---------|
| SIMPLE | Per-database logical snapshot. Restore to the backup point only. |
| FULL | Per-database logical snapshot plus per-database logical LOG chain. Restore to any captured transaction timestamp. |

This changes the old meaning of FULL. FULL is no longer "physical files plus
filtered WAL". FULL is "logical base plus logical PITR logs".

---

## Non-Negotiable Constraints

1. No cluster-scoped raw WAL in normal `.bak` files.
2. No copied `global/`, `pg_xact/`, `pg_multixact/`, `pg_subtrans/`,
   `pg_commit_ts/`, or `pg_control` in normal `.bak` files.
3. No physical PostgreSQL startup recovery for the normal per-database path.
4. LOG backups must be database-scoped by construction.
5. Restore must apply complete committed transactions only.
6. If a feature cannot be represented logically, backup must reject it or mark
   the backup as requiring an explicit adapter.

---

## v1 Support Gate

`SUPPORT_MATRIX.md` is part of the v1 contract. FULL, DIFFERENTIAL, and LOG
backups validate the current database before writing output or advancing the
logical chain.

The rule is simple:

- Supported PostgreSQL and TimescaleDB features must have restore tests.
- Unsupported physical/external features must raise `feature_not_supported`
  before backup.
- Silent omission is a bug.

Current supported PostgreSQL coverage includes triggers, RLS policies,
partitioned tables, identity/generated columns, domains, enums, views, rewrite
rules, sequences, large objects, grants/owners, extensions, and LOG-window DDL.

Current hard rejections include unlogged tables, foreign tables, regular
materialized views, ordinary table inheritance, user event triggers, custom
range/base/pseudo types, custom text search configurations, user aggregates,
publications, and subscriptions.

---

## High-Level Architecture

```
Source database
  |
  | FULL backup
  |   - schema/metadata
  |   - table data snapshot
  |   - slot/checkpoint metadata
  v
full.bak
  |
  | LOG backup(s)
  |   - logical transactions for this database only
  |   - DDL frames
  |   - sequence frames
  |   - extension adapter frames
  v
log_001.bak, log_002.bak, ...
  |
  | Restore
  |   - create temp DB
  |   - load FULL snapshot
  |   - replay LOG transactions until stop_at
  |   - rebuild derived indexes
  |   - run extension post-restore adapters
  |   - rename temp DB to target
  v
Target database
```

Logical decoding slots are database-scoped, so they are the correct primitive
for per-database LOG backups. FULL-mode chain slots must be PostgreSQL
failover logical slots so a correctly configured physical standby can
synchronize the slot and continue the backup chain after promotion.

---

## `.bak` Format v1

The existing container format stays: magic bytes, JSON header, framed sections,
optional zstd compression, optional AES-256-GCM encryption, per-entry checksums,
whole-file checksum.

The section model changes.

| Section | FULL | DIFF | LOG | Purpose |
|---------|------|------|-----|---------|
| `METADATA` | yes | yes | optional | Extensions, roles referenced, grants, DB settings, support manifest. |
| `SCHEMA` | yes | yes | no | Replayable DDL for database objects. |
| `DATA` | yes | yes | no | Table snapshots using binary COPY payloads. |
| `MANIFEST` | yes | yes | yes | Object inventory, dependencies, feature flags, adapter requirements. |
| `LOGICAL_STREAM` | no | optional | yes | Transactional logical changes for this database. |
| `DDL_STREAM` | no | optional | yes | Ordered DDL/event frames that must replay with the log. |
| `SEQUENCE_STREAM` | yes | optional | yes | Sequence state snapshots and sequence changes. |
| `LARGE_OBJECTS` | yes | optional | yes | Large object snapshots and changes, if supported. |
| `EXTENSION_STREAM` | yes | optional | yes | Adapter-specific payloads, e.g. TimescaleDB. |

Removed from normal per-database backups:

- `WAL_SEGMENTS`
- raw 16 MB WAL files
- `global/`
- `pg_xact/`, `pg_multixact/`, `pg_subtrans/`, `pg_commit_ts/`
- `pg_control`

### Header Fields

Required header fields:

- `format_version`
- `backup_id`
- `chain_id`
- `mode`
- `type`
- `db_name`
- `db_oid`
- `pg_version`
- `created_at`
- `base_snapshot_lsn`
- `start_lsn`
- `stop_lsn`
- `stop_commit_time`
- `previous_stop_lsn`
- `logical_plugin`
- `logical_slot_name`
- `logical_slot_failover`
- `requires_wal_level`
- `compressed`
- `encrypted`
- `checksum_algo`
- `feature_manifest_hash`

The header must be inspectable without the password and must not contain table
data, secrets, raw row values, or role passwords.

---

## Logical Stream

LOG backups store a sequence of frames. Frames are compressed/encrypted as a
section payload, but remain structured internally.

Core frame types:

| Frame | Purpose |
|-------|---------|
| `BEGIN` | Start transaction, includes xid and first LSN. |
| `RELATION` | Relation identity, columns, types, replica identity, adapter tag. |
| `INSERT` | Insert row values. |
| `UPDATE` | Old key/full old row plus new row values. |
| `DELETE` | Old key/full old row. |
| `TRUNCATE` | Truncate one or more relations. |
| `DDL` | Ordered DDL command or normalized DDL event. |
| `SEQUENCE` | Sequence state after transaction. |
| `LARGE_OBJECT` | Large object create/update/delete chunk. |
| `EXTENSION` | Adapter-owned frame. |
| `COMMIT` | Commit LSN and commit timestamp. |
| `ABORT` | Optional diagnostic frame; restore ignores aborted changes. |

Restore rule:

- Apply a transaction only if it commits and `commit_time <= stop_at`.
- Stop before the first committed transaction with `commit_time > stop_at`.
- Never apply half a transaction.

If `track_commit_timestamp` is unavailable, the output plugin must use the
commit timestamp available to logical decoding. If neither is available, PITR
by timestamp is unsupported and backup must reject FULL mode.

---

## Source Capture

### FULL Backup

1. Verify `wal_level = logical`.
2. Create or reuse a chain-owned failover logical replication slot for the
   database.
3. Export a consistent snapshot tied to the slot start LSN.
4. Dump schema, metadata, manifest, sequence state, large objects, and table
   data under that snapshot.
5. Record `base_snapshot_lsn` and initialize chain metadata.
6. Keep the failover logical slot until LOG backups have advanced the chain or
   the chain is explicitly closed.

The FULL `.bak` contains only the selected database's logical contents.

### LOG Backup

1. Read chain metadata and the previous confirmed LSN.
2. Verify the chain slot exists, is owned by `pg_dbbackup`, and has
   `failover = true`.
3. Decode changes from the chain slot up to a safe stop LSN.
4. Write logical frames for the selected database only.
5. Persist the `.bak` and verify its checksum.
6. Advance the slot only after the `.bak` is durable.
7. Update chain metadata with `stop_lsn` and `stop_commit_time`.

### HA Failover

The extension creates `_pg_dbbackup_<dboid>` as a failover logical slot. Actual
slot survival across primary promotion is a PostgreSQL cluster responsibility:
the primary/standby pair must use a physical replication slot,
`hot_standby_feedback = on`, `sync_replication_slots = on` on the standby, and
`synchronized_standby_slots` on the primary. After promotion, backup jobs must
connect directly to the promoted primary.

Before planned promotion, operators should call
`dbbackup.pg_dbbackup_failover_slot_status('<db>')` or
`dbbackup.pg_dbbackup_failover_slot_ready('<db>')` on the candidate standby.
Only `synced = true`, `temporary = false`, and `invalidation_reason IS NULL`
is a safe continuation point. If the promoted primary does not have that
persistent synced failover slot, LOG/DIFFERENTIAL backup must refuse to
continue and a new FULL backup is required.

### PgDog And SQL Proxies

PgDog is a routing layer, not a PostgreSQL recovery mechanism. Backup jobs must
be routed to the current primary because FULL/DIFFERENTIAL/LOG backup writes
server-side files and manages logical replication slots. If PgDog sends the
request to a hot standby, PostgreSQL rejects it; the extension must not try to
discover and contact the primary from inside a backend process.

Required behavior:

- a PgDog route targeting the primary can run FULL backup successfully
- a direct standby connection rejects backup
- a PgDog route targeting the standby rejects backup
- standby read sessions must still work when `pg_dbbackup` is preloaded and a
  FULL chain exists, so transaction-end journals skip recovery/read-only
  transactions

### DIFFERENTIAL Backup

DIFFERENTIAL is implemented as a cumulative logical stream since the base FULL.
It is a restore-speed optimization, not a PITR requirement.

---

## DDL Capture

PostgreSQL logical replication does not automatically replicate DDL. This
extension must capture DDL itself.

Implemented mechanism:

- Event triggers for `ddl_command_end` and `sql_drop`.
- A `dbbackup.ddl_log` catalog for inspection and replay diagnostics.
- The logical output plugin decodes `dbbackup.ddl_log` rows specially, so DDL
  is ordered transactionally with DML.
- A future `ProcessUtility_hook` can be added only for command families proven
  insufficient through tests.

DDL frames must include:

- original command text where safe and useful
- normalized object identity
- schema-qualified object names
- dependency information
- extension ownership flag
- adapter ownership flag

DDL replay must be idempotent where possible, but it must not hide
incompatibilities. A failed DDL replay fails restore.

---

## Sequence Capture

PostgreSQL logical replication does not keep sequence state synchronized.

Required behavior:

- FULL stores all sequence definitions and current states.
- LOG stores sequence state changes for sequences used after the FULL.
- Restore sets sequences after applying all DML up to `stop_at`.
- Identity columns are handled through their backing sequences.

Implemented behavior:

- FULL stores sequence definitions and current states.
- A transaction-end journal records changed sequence state in
  `dbbackup.sequence_log`.
- The logical output plugin emits `setval` frames.
- Restore applies the latest sequence state at or before `stop_at`.

---

## Large Objects

Large objects are not covered by ordinary table logical replication.

Implemented behavior:

- FULL snapshots large-object metadata, pages, ownership, grants, and comments.
- A transaction-end journal records changed or unlinked large objects in
  `dbbackup.large_object_log`.
- LOG replay recreates the latest object snapshot or applies unlink behavior
  transactionally with the surrounding logical stream.

---

## Derived Objects

Indexes are derived state and should normally be rebuilt, not replayed from
physical pages.

Restore policy:

- Load tables first.
- Apply logical LOG transactions.
- Recreate indexes, constraints, materialized views, and extension-derived
  indexes at the final PITR state.
- For performance, allow indexes to be created before replay only when the
  replay engine applies SQL DML through PostgreSQL and the behavior is tested.

This is important for:

- `pgvector` HNSW and IVFFlat indexes
- `pg_textsearch` BM25 indexes
- expression indexes
- partial indexes
- partitioned indexes

---

## Extension Adapter Model

Every extension is assigned one support tier.

| Tier | Meaning |
|------|---------|
| `transparent` | Works through normal schema/data/logical replay. |
| `derived-index` | Data is logical; indexes are rebuilt after replay. |
| `adapter` | Needs extension-specific capture/replay hooks. |
| `blocked` | Backup rejects when the feature is present. |

The manifest records required adapters and versions. Restore validates that
required extensions and adapter versions are available before applying data.

### pgvector

Tier: `derived-index`

Plan:

- Vector column data is regular table data.
- HNSW and IVFFlat indexes are rebuilt after restore.
- Similarity query tests verify semantic correctness.

### pg_textsearch

Tier: `derived-index`

Plan:

- Table data is restored logically.
- BM25 index files, segments, memtables, and CTID maps are not authoritative.
- BM25 indexes are rebuilt after replay.
- `shared_preload_libraries` must include `pg_textsearch` on the restore
  target.

### TimescaleDB

Tier: `adapter`

TimescaleDB is not just "normal tables". Hypertables, chunks, compression,
continuous aggregates, background jobs, and policies require adapter logic.

Adapter responsibilities:

1. Run Timescale restore pre/post hooks when available.
2. Restore extension version and validate binary compatibility.
3. Capture hypertable definitions, dimensions, chunk metadata, compression
   settings, policies, jobs, and continuous aggregate definitions.
4. During LOG replay, map chunk-origin changes back to the hypertable root
   where possible.
5. Disable background jobs/policies during restore replay.
6. Rebuild or refresh continuous aggregates after replay unless exact
   invalidation-log replay is implemented and tested.
7. Recompress chunks after replay if compression state cannot be safely
   replayed transaction-by-transaction.
8. Fail loudly for Timescale features not covered by tests.

Current v1 coverage is green for hypertables, chunk DML replay through the
root hypertable, multidimensional range/hash dimensions, continuous
aggregates, retention policies, compression policies, refresh policies,
FULL+LOG restore, and `stop_at` filtering.

---

## PostgreSQL Feature Test Matrix

The project should not claim "PostgreSQL feature complete" without tests across
these feature families.

### Core DML and Transactions

- INSERT, UPDATE, DELETE, TRUNCATE
- multi-row statements
- `INSERT ... ON CONFLICT`
- transaction rollback
- savepoint rollback
- transaction touching multiple tables
- transaction with DDL plus DML
- stop_at before, inside, and after a LOG backup
- stop_at on exact commit boundary
- aborted transactions are not applied

### Table Shapes

- heap table
- partitioned table
- inherited table
- table with TOAST values
- generated columns
- identity columns
- default expressions
- arrays, JSONB, ranges, enums, domains, composites
- collations
- nullable and NOT NULL columns
- dropped columns

### Constraints and Indexes

- primary key
- unique
- foreign key
- deferrable foreign key
- check
- exclusion
- expression index
- partial index
- covering index
- partitioned index
- concurrent index build, if allowed by capture hooks

### Schema Objects

- schemas
- views
- materialized views
- functions
- procedures
- triggers
- rules
- event triggers
- operators and operator classes
- casts
- text search dictionaries/configurations
- policies and row-level security
- grants and default privileges
- comments
- database-level settings

### Special Storage

- sequences
- large objects
- unlogged tables
- temporary tables
- foreign tables
- materialized view data

Support policy:

- Sequences: must be supported.
- Large objects: supported through FULL snapshots and LOG snapshot/unlink
  frames.
- Unlogged tables: FULL snapshot may be supported; PITR changes must be
  rejected unless a logical path is implemented.
- Temporary tables: never backed up.
- Foreign tables: DDL only unless an adapter is implemented.
- Materialized views: definition plus refresh after restore unless data replay
  is explicitly implemented.

---

## TimescaleDB Test Matrix

TimescaleDB support requires tests for:

- extension create/restore with exact version validation
- hypertable create before FULL
- hypertable create after FULL and before LOG
- chunk creation during LOG window
- INSERT into hypertable
- UPDATE hypertable rows
- DELETE hypertable rows
- TRUNCATE hypertable
- multi-dimensional hypertable
- custom chunk interval
- retention policy
- compression or Hypercore enabled before FULL
- compression or Hypercore enabled during LOG window
- insert into compressed/columnstore-backed data when supported by Timescale
- continuous aggregate definition
- continuous aggregate refresh after restore
- background jobs disabled during restore and re-enabled after
- policies restored but not executed during replay
- restore to a fresh database with Timescale preloaded
- restore failure when Timescale is unavailable
- PITR before a chunk exists
- PITR after a chunk exists
- PITR before and after compression policy effects

If a Timescale feature cannot be supported, the test must assert that
backup rejects it with a clear error.

Current v1 coverage includes snapshot restore for hypertables, compressed
chunks, continuous aggregates, and extension version preservation; LOG/PITR
coverage includes INSERT-driven chunk creation and timestamp stop filtering.
The remaining matrix items above are still required before claiming complete
TimescaleDB feature coverage.

---

## Isolation and Security Tests

The key product promise is per-database isolation. Add tests that prove it.

- Create 100 databases.
- Put a unique secret string in 99 non-target databases.
- Generate heavy writes in non-target databases.
- Back up the target database in FULL mode.
- Verify the `.bak` does not contain non-target secret strings in plaintext
  when unencrypted.
- Verify filelist/manifest contains no non-target database objects.
- Restore the target and verify only target data is present.
- Repeat while non-target databases generate high WAL volume.

This test prevents accidental reintroduction of raw WAL segments or cluster
state into normal `.bak` files.

---

## Restore Algorithm

1. Read and validate all headers.
2. Validate same `chain_id`, same source database identity, continuous LSNs,
   compatible PostgreSQL versions, and required adapters.
3. Create temp database from `template0`.
4. Install required extensions in dependency order.
5. Apply schema and metadata.
6. Load FULL DATA.
7. Apply optional DIFF DATA.
8. Replay LOG transactions in order until `stop_at`.
9. Apply final sequence states.
10. Rebuild derived indexes.
11. Refresh materialized views or adapter-owned derived objects.
12. Run extension post-restore adapters.
13. Validate row counts/checksums/manifests where available.
14. Drop existing target database if requested.
15. Rename temp database to target.
16. Drop temp database on error.

---

## Implementation Phases

### Phase 0: Remove Cluster-Scoped PITR From Default Path

- Done: raw WAL segment sections are not written by normal FULL/DIFF/LOG
  backups.
- Done: `global/`, SLRU, and `pg_control` are not captured by normal FULL
  backups.
- Done: subprocess native recovery and physical WAL replay were removed from
  the v1 build/source path.
- Done: isolation tests prove other database data does not enter `.bak`.

### Phase 1: `.bak` v1 Logical Sections

- Done: v1 normal path uses metadata, schema, data, and logical stream
  sections. DDL, sequence, large-object, and extension-adapter frames are
  represented in the logical stream and metadata sections.
- Done: reader/writer tests cover the v1 section set.

### Phase 2: Chain and Slot Catalog

- Done: chain catalog table and deterministic slot naming are implemented.
- Done: logical slots are created, consumed, and tracked per database.
- Done: restore validates database name, backup type, differential placement,
  and LSN continuity.

### Phase 3: Logical Output Plugin

- Done: DML, truncate, DDL journal, sequence journal, and large-object journal
  rows are emitted as replayable SQL frames.
- Done: commit timestamp/LSN is emitted and used for PITR stop decisions.
- Done: tables without replica identity are rejected for UPDATE/DELETE.

### Phase 4: FULL Snapshot Tied to Slot

- Done: FULL snapshots are anchored to a logical slot start LSN.
- Done: schema/data/metadata are dumped into the v1 `.bak`.
- Done: LOG replay starts from the recorded chain position.

### Phase 5: LOG Backup Writer

- Done: LOG/DIFF decode from the previous chain LSN to a safe stop LSN.
- Done: logical stream frames are written directly to the `.bak`.
- Done: the slot is consumed through the same logical decode used for the
  backup, avoiding a second unsafe decode pass.

### Phase 6: Logical Replay Engine

- Done: frames apply into a temp database.
- Done: replay respects transaction commit boundaries and `stop_at`.
- Done: multi-LOG, branch-after-restore, and failure-cleanup tests pass.

### Phase 7: DDL, Sequence, and Large Object Support

- Done: DDL, sequence state, and large objects are captured and replayed.
- Done: tests cover DDL during LOG windows, standalone sequence PITR, and
  large-object write/unlink behavior.

### Phase 8: Derived Object Rebuild

- Defer/rebuild indexes after replay.
- Rebuild materialized views or refresh them.
- Add pgvector and pg_textsearch tests.

### Phase 9: TimescaleDB Adapter

- Done: Timescale metadata capture covers hypertables, dimensions,
  continuous aggregates, and policies.
- Done: chunk DML replays through the root hypertable.
- Done: Timescale test matrix currently passes.

### Phase 10: PostgreSQL Feature Matrix

- In progress: broad PostgreSQL feature tests now cover core DML,
  transactions, DDL replay, indexes, roles/grants, extensions, sequences,
  large objects, cross-container restore, and large indexed tables.
- Continue adding green tests or explicit rejection tests for each newly found
  feature family.

### Phase 11: Performance and Operations

- Done: plain sections stream directly to disk.
- Done: table backup avoids table-sized backend buffers by chunking COPY temp
  files into the `.bak`.
- Done: FULL restore streams DATA entries into `COPY FROM` and verifies entry
  checksums incrementally.
- Done: logical replay uses one libpq round trip per committed transaction.
- Implement streaming zstd/AES section framing.
- Avoid the final full-file reread for footer checksums by changing the frame
  layout so lengths/checksums are known before bytes are hashed.
- Parallel table load.
- Progress NOTICEs.
- Chain inspection functions.
- Slot lag reporting.
- Backup size reporting.

### Phase 12: Documentation and Migration

- Update README and SQL reference.
- Document physical/native PITR as removed from the normal path or as an
  explicit experimental mode if retained.
- Document exact support matrix.
- Document operational requirements and failure modes.

---

## Definition of Done

The logical PITR design is complete when:

1. Done: FULL + LOG restores pass for plain PostgreSQL tables.
2. Done: `FullRestore_Pitr_Stops_At_Timestamp` passes without raw WAL
   segments, cluster state, or subprocess native recovery.
3. Done: isolation tests prove other database data does not enter target
   `.bak`.
4. In progress: PostgreSQL feature matrix expands by test, with unsupported
   behavior rejected explicitly.
5. Done for current v1 scope: TimescaleDB matrix is green.
6. Done: pgvector and pg_textsearch restore by rebuilding derived indexes.
7. Done: failed restores leave no temp database behind.
8. Done: README states the support matrix for this v1 implementation.

---

## Key PostgreSQL References

Local PostgreSQL source is expected at `D:\Sources\postgres`.

Important areas:

| Topic | Source area |
|-------|-------------|
| Logical decoding API | `src/backend/replication/logical/` |
| Output plugins | `src/include/replication/output_plugin.h` |
| Replication slots | `src/backend/replication/slot.c` |
| SQL slot functions | `src/backend/replication/logical/logicalfuncs.c` |
| Event triggers | `src/backend/commands/event_trigger.c` |
| ProcessUtility hook | `src/backend/tcop/utility.c` |
| pg_dump schema logic | `src/bin/pg_dump/pg_dump.c` |
| COPY binary | `src/backend/commands/copy*.c` |
| Large objects | `src/backend/storage/large_object/` |
| Sequence internals | `src/backend/commands/sequence.c` |
