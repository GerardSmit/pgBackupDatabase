# pg_dbbackup: Per-Database PITR Backup Extension for PostgreSQL

## Design Decisions Log

### Decision 1: Native PG Extension (not external tool)
**Options considered:** C# CLI tool, PowerShell scripts, native PG C extension.
**Chosen:** Native C extension. `CREATE EXTENSION pg_dbbackup` — works everywhere PG runs, no external dependencies, uses PG internal APIs directly (XLogReader, do_pg_backup_start/stop, copydir).

### Decision 2: Foreground execution (not background worker)
**Options considered:** Dynamic background worker, foreground SQL function.
**Chosen:** Foreground. Verified that `do_pg_backup_start/stop`, `XLogReaderAllocate`, `copydir()` all work from regular backend processes — no walsender/recovery guards. Simpler architecture. Async variant deferred to last phase.

### Decision 3: SIMPLE + FULL recovery modes
**Inspired by:** SQL Server's per-database recovery models.
**SIMPLE:** Logical backup (DDL + COPY binary). Smaller, faster restore, cross-PG-version. No PITR.
**FULL:** Physical backup (files + filtered WAL). PITR enabled. Block-level differential.
Both require `wal_level = replica` — SIMPLE needs it for incremental change detection via WAL summaries.

### Decision 4: Three backup types (SQL Server naming)
**Options considered:** Separate DIFF/INCREMENTAL/LOG, merged DIFF+INCREMENTAL, two types only.
**Chosen:** Three types matching SQL Server: FULL, DIFFERENTIAL, LOG.
- DIFFERENTIAL = changes since last FULL (cumulative, both modes). Merged "incremental" (SIMPLE) and "differential" (FULL) under one name.
- LOG = WAL-only since last backup (FULL mode only). Lightweight PITR extension.

### Decision 5: Single .bak file format
**Options considered:** Directory layout, tar archive, custom binary.
**Chosen:** Custom `.bak` binary format. One self-contained file per backup. Portable. Magic bytes at both ends. JSON header (always readable). Sections: METADATA, SCHEMA, DATA, WAL. SHA-256 checksums.

### Decision 6: Compression + encryption in v1
**Compression:** zstd per-section. Default on. Header always uncompressed.
**Encryption:** AES-256-GCM with PBKDF2 key derivation. Password-based. Salt+IV per file in header. Compress first, then encrypt.

### Decision 7: Restore via temp DB + rename
**Options considered:** Shadow cluster (initdb), in-place restore, temp DB + rename.
**Chosen:** Create temp DB → inject data → rename. Supports replacing existing DB safely. `ALTER DATABASE RENAME` verified: needs AccessExclusiveLock + zero connections. Cleanup drops temp DB on error.

### Decision 8: WAL filtering by database OID
**Core insight:** Every WAL record's block references contain `RelFileLocator { spcOid, dbOid, relNumber }`. Filter by `dbOid` + always include cluster-wide rmgrs (XLOG, XACT, CLOG, MULTIXACT, STANDBY, DBASE, RELMAP).

### Decision 9: Extension compatibility verified
**TimescaleDB:** Standard heap tables, no custom WAL rmgrs. Track extension version in metadata.
**pgvector:** Standard heap + custom index AMs. FULL mode preserves index files.
**pg_textsearch:** Standard heap + BM25 index (Generic XLOG). Segment files captured in FULL mode.
**pgdog:** External proxy, no DB state. No impact.

---

## Context

PostgreSQL has no per-database physical backup. `pg_basebackup` = cluster-level only. `pg_dump` = logical only (no PITR). SQL Server has `BACKUP DATABASE`, `BACKUP LOG`, `RESTORE DATABASE ... FROM DISK` with `.bak` files — per-database, online, portable.

**Goal:** Native PostgreSQL C extension. `CREATE EXTENSION pg_dbbackup`. SQL Server-style `.bak` files and backup types (FULL, DIFFERENTIAL, LOG). Two recovery modes (SIMPLE/FULL). Compression + encryption. Transfer `.bak` files between engines.

**Prerequisite:** `wal_level = replica` for both modes.

---

## Recovery Modes

| | SIMPLE | FULL |
|---|--------|------|
| Backup format | Logical (DDL + COPY binary) in `.bak` | Physical (files + filtered WAL) in `.bak` |
| Full | Yes | Yes |
| Differential | Yes (re-dump changed tables) | Yes (changed blocks + WAL) |
| Log | **No** | Yes (WAL-only, extends PITR) |
| PITR | **No** — point-of-backup only | **Yes** — any timestamp |
| Cross-PG-version restore | **Yes** | No |

---

## Backup Types (SQL Server naming)

### FULL
Complete backup. Both modes.
- SIMPLE: DDL + COPY binary of all tables
- FULL: all database files + WAL extract for backup window

### DIFFERENTIAL
Changes since last FULL backup (cumulative). Both modes. Only need latest DIFF — not a chain.
- SIMPLE: re-dump changed tables (detected via WAL summaries PG17+ or file mtime)
- FULL: changed blocks + WAL extract for `[full_stop_lsn, diff_stop_lsn]`

### LOG
WAL since last backup of any type. **FULL mode only.** Sequential chain — need ALL log .baks.
- WAL extract only, no data copy
- Lightweight — extends PITR window between diffs

### Backup chain patterns

```
SIMPLE:  FULL.bak ─── DIFF.bak ─── DIFF.bak ─── FULL.bak
         Restore = FULL + latest DIFF

FULL:    FULL.bak ─── DIFF.bak ─── LOG.bak ─── LOG.bak ─── FULL.bak
         Restore = FULL + latest DIFF + all LOGs after DIFF
                   ◄────────────── PITR anywhere in range ──────────────►
```

---

## SQL Interface

```sql
-- Configure recovery mode per database
SELECT pg_dbbackup_set_mode('mydb', 'simple');
SELECT pg_dbbackup_set_mode('mydb', 'full');
SELECT pg_dbbackup_get_mode('mydb');

-- BACKUP DATABASE (each call = one .bak file)
SELECT pg_dbbackup('mydb', '/backup/mydb_full.bak');
SELECT pg_dbbackup('mydb', '/backup/mydb_diff.bak', type := 'differential');
SELECT pg_dbbackup('mydb', '/backup/mydb_log.bak', type := 'log');

-- With compression + encryption
SELECT pg_dbbackup('mydb', '/backup/mydb.bak',
    compress := true,              -- default: true
    password := 'my-secret');      -- default: NULL (no encryption)

-- RESTORE DATABASE (provide .bak files in order)
SELECT pg_dbrestore('mydb',
    files := ARRAY['/backup/full.bak', '/backup/diff.bak',
                   '/backup/log1.bak', '/backup/log2.bak'],
    stop_at := '2026-01-15 14:30:00',
    password := 'my-secret');

-- Restore to different name
SELECT pg_dbrestore('mydb',
    files := ARRAY['/backup/full.bak'],
    target_db := 'mydb_copy');

-- Inspect .bak files (like SQL Server RESTORE HEADERONLY / FILELISTONLY)
SELECT * FROM pg_dbbackup_header('/backup/mydb.bak');
SELECT * FROM pg_dbbackup_filelist('/backup/mydb.bak');

-- Verify .bak file integrity
SELECT * FROM pg_dbbackup_verify('/backup/mydb.bak');
```

**Validation:**
- `type := 'log'` with SIMPLE mode → error
- `stop_at` on SIMPLE backups → error: "PITR requires FULL mode"
- Restore validates chain: first = FULL, then DIFF (optional), then LOGs; LSN continuity checked
- Default mode: SIMPLE
- `password` required on restore if `.bak` was encrypted

---

## .bak File Format

Single self-contained binary file. Portable across machines.

### Binary Layout

```
┌─────────────────────────────────────────┐
│ Magic: "PGBAK" (5 bytes)                │
│ Format Version: uint16                  │
│ Header Length: uint32                   │
├─────────────────────────────────────────┤
│ Header (JSON, uncompressed, unencrypted)│
│   mode, type, db_name, db_oid,          │
│   start_lsn, stop_lsn, timeline,       │
│   pg_version, created_at,              │
│   base_backup_lsn (for diff/log),      │
│   compressed, compression_algo,         │
│   encrypted, encryption_algo,           │
│   key_salt (hex), key_iv (hex),         │
│   file_count, checksum_algo             │
├─────────────────────────────────────────┤
│ Section: METADATA                       │
│   Type: 0x01 | Length: uint64           │
│   [compressed then encrypted if set]    │
│   Data: SQL script (extensions, grants) │
├─────────────────────────────────────────┤
│ Section: SCHEMA (SIMPLE mode only)      │
│   Type: 0x02 | Length: uint64           │
│   Data: DDL SQL script                  │
├─────────────────────────────────────────┤
│ Section: DATA                           │
│   Type: 0x03 | Entry count: uint32     │
│   Per entry:                            │
│     Path: uint16 len + bytes            │
│     Data: uint64 len + bytes            │
│     Checksum: SHA-256 (32 bytes)        │
├─────────────────────────────────────────┤
│ Section: WAL (FULL mode, full/diff/log) │
│   Type: 0x04 | Length: uint64           │
│   Data: (uint32 len, XLogRecord) pairs  │
├─────────────────────────────────────────┤
│ Footer:                                 │
│   File checksum: SHA-256 (32 bytes)     │
│   Magic: "PGBAK" (5 bytes)             │
└─────────────────────────────────────────┘
```

**Properties:**
- Header always uncompressed + unencrypted (inspectable, contains no sensitive data)
- Salt + IV in header → same password = different ciphertext per file
- Sections: compress first (zstd), then encrypt (AES-256-GCM)
- Magic at both ends → detects truncation
- SHA-256 per data entry + whole-file checksum → detects corruption
- 64-bit lengths → supports files >2GB
- Streamable: sequential write, no seeking during backup

### Compression

- Algorithm: zstd (fast, good ratio, already in PG ecosystem)
- Applied per-section (header always uncompressed)
- `compress := true` (default) / `compress := false`
- Stored in header: `"compressed": true, "compression_algo": "zstd"`

### Encryption

- Algorithm: AES-256-GCM (authenticated — detects tampering)
- Key derivation: PBKDF2-HMAC-SHA256 from password + salt (PG has OpenSSL)
- Applied per-section after compression (header never encrypted)
- Salt (16 bytes) + IV (12 bytes) generated per file, stored in header
- `password := 'secret'` enables encryption; NULL = plaintext
- Restore with wrong password → error: "authentication failed" (GCM detects it)

---

## .bak Contents Per Type

| Type | METADATA | SCHEMA | DATA | WAL |
|------|----------|--------|------|-----|
| SIMPLE FULL | Yes | DDL for all objects | COPY binary per table | — |
| SIMPLE DIFF | Yes | DDL for new/changed | Only changed tables | — |
| FULL FULL | Yes | — | All relation files + pg_filenode.map | WAL `[start, stop]` |
| FULL DIFF | Yes | — | Changed blocks/files | WAL `[base_stop, diff_stop]` |
| FULL LOG | Yes | — | — | WAL `[prev_stop, current]` |

---

## Architecture

### Execution Model

Foreground execution. `do_pg_backup_start/stop`, `XLogReaderAllocate`, `copydir()`, SPI all verified to work from regular backend. Synchronous, interruptible via `CHECK_FOR_INTERRUPTS()`.

### Mode Configuration

```sql
CREATE TABLE pg_dbbackup.db_config (
    db_oid  oid PRIMARY KEY,
    db_name text NOT NULL,
    mode    text NOT NULL DEFAULT 'simple' CHECK (mode IN ('simple', 'full'))
);
```

### Backup Flows

**SIMPLE FULL → .bak:**
1. `AccessShareLock` on database
2. `BEGIN ISOLATION LEVEL REPEATABLE READ`
3. Open `.bak` file, write magic + header
4. Generate + write METADATA section (extensions, grants, ACLs)
5. Generate + write SCHEMA section (DDL, dependency-ordered via `pg_depend`)
6. Write DATA section: `COPY table TO STDOUT (FORMAT binary)` per table
7. `COMMIT`
8. Write footer checksum + magic

**SIMPLE DIFFERENTIAL → .bak:**
1. Read base FULL `.bak` header → get timestamp + table list
2. Detect changed tables (WAL summaries PG17+ or file mtime)
3. Same flow but only changed tables + new/altered DDL

**FULL FULL → .bak:**
1. `AccessShareLock` on database
2. Create temp replication slot to pin WAL
3. `do_pg_backup_start("pg_dbbackup", true, NULL, &bstate, NULL)`
4. Open `.bak`, write magic + header
5. Write METADATA section
6. Write DATA section: read files from `base/{dboid}/`, tablespaces, `global/pg_filenode.map`
7. `do_pg_backup_stop(&bstate, true)` → `stop_lsn`
8. Write WAL section: XLogReader filters WAL `[start_lsn, stop_lsn]`
9. Write footer
10. Drop replication slot

**FULL DIFFERENTIAL → .bak:**
- Read base FULL `.bak` header → `stop_lsn`
- WAL summaries or mtime → changed blocks for `dbOid`
- DATA section: only changed files/blocks
- WAL section: filter `[base_stop_lsn, diff_stop_lsn]`

**FULL LOG → .bak:**
- `pg_switch_wal()` to close current segment
- WAL section only: filter `[prev_stop_lsn, current_lsn]`
- No DATA section

### Restore Flow

```sql
SELECT pg_dbrestore('mydb',
    files := ARRAY['/full.bak', '/diff.bak', '/log1.bak', '/log2.bak'],
    stop_at := '2026-01-15 14:30:00',
    password := 'secret');
```

1. Read + validate header from each `.bak`:
   - First must be FULL
   - Optional DIFF (latest only, cumulative)
   - Optional LOG chain (all needed, sequential)
   - LSN continuity
   - Same `db_name` across all
   - Decrypt check (wrong password → immediate error)
2. Create temp database `_pg_dbbackup_restore_{random}` from `template0`

**SIMPLE restore:**
3. Connect to temp database
4. Execute SCHEMA from FULL `.bak` (+ DDL changes from DIFF if present)
5. COPY FROM for each table in FULL `.bak`
6. Apply DIFF `.bak`: truncate + COPY FROM changed tables
7. Execute METADATA (extensions, grants)

**FULL restore:**
3. `AccessExclusiveLock` on temp database
4. Clear `base/{temp_dboid}/`
5. Extract DATA from FULL `.bak` → write to `base/{temp_dboid}/`
6. Apply DIFF `.bak` DATA: overwrite changed files/blocks
7. Replay WAL from each `.bak` in order (FULL WAL → DIFF WAL → LOG WALs):
   - OID remap `dbOid` original → temp
   - Stop at first commit where `xact_time > stop_at` (PITR)
8. Update `pg_database`: `datfrozenxid`, `datminmxid`
9. Execute METADATA

**Both modes (final):**
10. If target exists: `DROP DATABASE target_db`
11. `ALTER DATABASE _pg_dbbackup_restore_{random} RENAME TO target_db`
12. On error: `DROP DATABASE _pg_dbbackup_restore_{random}`

---

## WAL Filter (FULL mode only)

Include if ANY of:

| Condition | Why |
|-----------|-----|
| Block ref `rlocator.dbOid == targetDbOid` | Database data |
| `xl_rmid` in {XLOG, XACT, CLOG, MULTIXACT, STANDBY} | Cluster-wide consistency |
| `xl_rmid == RM_DBASE_ID` | Database create/drop |
| `xl_rmid == RM_RELMAP_ID` | Relation map updates |

```c
static bool
wal_filter_should_include(DecodedXLogRecord *rec, Oid target_db_oid)
{
    RmgrId rmid = rec->header.xl_rmid;

    if (rmid == RM_XLOG_ID || rmid == RM_XACT_ID || rmid == RM_CLOG_ID ||
        rmid == RM_MULTIXACT_ID || rmid == RM_STANDBY_ID ||
        rmid == RM_DBASE_ID || rmid == RM_RELMAP_ID)
        return true;

    for (int i = 0; i <= rec->max_block_id; i++)
    {
        DecodedBkpBlock *blk = &rec->blocks[i];
        if (blk->in_use && blk->rlocator.dbOid == target_db_oid)
            return true;
    }
    return false;
}
```

---

## Database Metadata (Both Modes)

METADATA section in every `.bak`:

| What | Source | Restore |
|------|--------|---------|
| Extensions | `pg_extension` | `CREATE EXTENSION IF NOT EXISTS` |
| Extension versions | `extversion` | `ALTER EXTENSION ... UPDATE TO` |
| Schemas | `pg_namespace` | `CREATE SCHEMA` |
| Role grants on DB | `pg_database` ACL | `GRANT ... ON DATABASE` |
| Default ACLs | `pg_default_acl` | `ALTER DEFAULT PRIVILEGES` |
| Object grants | `information_schema` | `GRANT ... ON ...` |
| Comments | `pg_description` | `COMMENT ON` |
| DB-level config | `pg_db_role_setting` | `ALTER DATABASE SET` |

Roles are cluster-level. Backup records referenced roles. Restore warns if missing.

---

## Project Structure

```
D:\Sources\pgBackupDatabase\
  pg_dbbackup.control
  Makefile
  sql/
    pg_dbbackup--1.0.0.sql
  src/
    pg_dbbackup.c                       # _PG_init, PG_MODULE_MAGIC, SQL entries
    bakfile.c / bakfile.h               # .bak format: write/read/verify
    bakfile_crypto.c / bakfile_crypto.h # Compression (zstd) + encryption (AES-256-GCM)
    backup_simple.c / backup_simple.h   # SIMPLE: full + differential
    backup_full.c / backup_full.h       # FULL: full + differential + log
    restore_simple.c / restore_simple.h # SIMPLE restore
    restore_full.c / restore_full.h     # FULL restore + PITR
    wal_filter.c / wal_filter.h         # XLogReader WAL filter
    ddl_gen.c / ddl_gen.h               # DDL generation from catalogs
    metadata_gen.c / metadata_gen.h     # metadata.sql generation
    inspect.c / inspect.h               # header/filelist/verify SRFs
    fileio.c / fileio.h                 # Safe file I/O wrappers
  test/
    conftest.py
    test_bakfile_format.py
    test_bakfile_crypto.py              # Compression + encryption tests
    test_simple_full.py
    test_simple_diff.py
    test_simple_restore.py
    test_full_backup.py
    test_full_diff.py
    test_full_log.py
    test_full_restore.py
    test_pitr.py
    test_metadata.py
    test_inspect.py                     # header/filelist/verify
    test_wal_filter.py
    test_replace_db.py
    test_mode_config.py
    test_cross_version.py
    test_portability.py                 # .bak transfer between machines
    test_timescaledb.py                 # TimescaleDB hypertable/chunk/cagg tests
    test_pgvector.py                    # pgvector HNSW/IVFFlat/vector data tests
    test_pg_textsearch.py               # pg_textsearch BM25 index tests
    test_pgdog.py                       # Backup through pgdog proxy
    test_multi_extension.py             # Combined extension scenarios
    helpers/
      pg_container.py
      time_helpers.py
      data_fixtures.py
```

---

## Test Strategy

### Framework

pytest + TestContainers against real PostgreSQL 17. Extension compiled + installed into container.

### PITR Time Control

`pg_sleep(1)` gaps + captured `now()` timestamps + `track_commit_timestamp = on`.

### Test Matrix

**Mode config + validation:**

| Test | Verifies |
|------|----------|
| `test_default_mode_simple` | Default SIMPLE |
| `test_set_mode_full` | Switch works |
| `test_log_rejected_simple` | LOG errors in SIMPLE |
| `test_pitr_rejected_simple` | stop_at errors on SIMPLE .bak |

**.bak format:**

| Test | Verifies |
|------|----------|
| `test_bak_magic_bytes` | PGBAK at start + end |
| `test_bak_header_json` | Header parseable |
| `test_bak_footer_checksum` | SHA-256 whole file |
| `test_bak_truncation_detected` | Missing end magic |
| `test_bak_corruption_detected` | Modified bytes caught |

**Compression + encryption:**

| Test | Verifies |
|------|----------|
| `test_compressed_smaller` | Compressed .bak < uncompressed |
| `test_compressed_roundtrip` | Compress → decompress = identical |
| `test_encrypted_not_readable` | Encrypted .bak sections not plaintext |
| `test_encrypted_roundtrip` | Encrypt → decrypt with correct password |
| `test_wrong_password_fails` | Wrong password → error |
| `test_same_password_different_ciphertext` | Different salt per file |
| `test_compressed_then_encrypted` | Both flags work together |

**SIMPLE mode:**

| Test | Verifies |
|------|----------|
| `test_simple_full_bak` | .bak created with SCHEMA + DATA |
| `test_simple_full_all_tables` | All tables in DATA section |
| `test_simple_full_custom_types` | Enums, domains in SCHEMA |
| `test_simple_full_concurrent` | Consistent snapshot |
| `test_simple_diff_only_changed` | DIFF .bak smaller, changed tables only |
| `test_simple_diff_new_tables` | New tables detected |
| `test_simple_restore_basic` | .bak → restore → data matches |
| `test_simple_restore_with_diff` | FULL + DIFF → correct state |
| `test_simple_restore_cross_version` | PG17 .bak restores on PG18 |
| `test_simple_restore_replaces` | Temp → rename workflow |

**FULL mode:**

| Test | Verifies |
|------|----------|
| `test_full_backup_bak` | .bak with DATA + WAL sections |
| `test_full_backup_all_forks` | Main, FSM, VM in .bak |
| `test_full_backup_concurrent` | Backup with concurrent DML |
| `test_full_diff_smaller` | DIFF .bak smaller than FULL |
| `test_full_log_wal_only` | LOG .bak has WAL, no DATA |
| `test_full_restore_basic` | Restore → data matches |
| `test_full_restore_chain` | FULL + DIFF + LOGs → correct |
| `test_pitr_stops_correctly` | Data after stop_at absent |
| `test_pitr_before_included` | Data before stop_at present |
| `test_pitr_tx_boundary` | Uncommitted not visible |
| `test_pitr_across_logs` | PITR in middle of LOG chain |

**Shared:**

| Test | Verifies |
|------|----------|
| `test_metadata_extensions` | Extensions restored |
| `test_metadata_grants` | Grants preserved |
| `test_metadata_settings` | DB config preserved |
| `test_metadata_missing_role` | Warning for missing roles |
| `test_chain_validation` | LSN gap → error |
| `test_chain_wrong_order` | Wrong .bak order → error |
| `test_cleanup_on_failure` | Temp DB dropped on error |
| `test_wal_filter_includes_db` | Correct WAL filtering |
| `test_wal_filter_excludes_other` | Other DB excluded |
| `test_concurrent_backups` | Parallel backups work |
| `test_requires_replica` | Error at wal_level=minimal |
| `test_bak_portable` | .bak from container A restores on B |
| `test_header_inspection` | pg_dbbackup_header() correct |
| `test_filelist_inspection` | pg_dbbackup_filelist() lists entries |
| `test_verify_valid` | pg_dbbackup_verify() passes |
| `test_verify_corrupt` | pg_dbbackup_verify() catches corruption |

**Extension compatibility (TimescaleDB, pgvector, pg_textsearch, pgdog):**

| Test | Verifies |
|------|----------|
| `test_timescaledb_hypertable_backup_simple` | Hypertable + chunks backed up in SIMPLE mode |
| `test_timescaledb_hypertable_restore_simple` | Hypertable queryable after SIMPLE restore |
| `test_timescaledb_hypertable_backup_full` | Hypertable files + WAL captured in FULL mode |
| `test_timescaledb_hypertable_restore_full` | Hypertable queryable after FULL restore |
| `test_timescaledb_continuous_aggregate` | Continuous aggregates survive backup/restore |
| `test_timescaledb_compression` | Compressed chunks restored correctly |
| `test_timescaledb_extension_version` | Extension version preserved in metadata.sql |
| `test_timescaledb_pitr` | PITR with TimescaleDB data at correct point |
| `test_pgvector_data_backup_simple` | Vector columns backed up correctly (SIMPLE) |
| `test_pgvector_data_restore_simple` | Vector data + similarity search works after restore |
| `test_pgvector_hnsw_index_full` | HNSW index files captured in FULL backup |
| `test_pgvector_hnsw_index_restore` | HNSW index usable after FULL restore (no rebuild) |
| `test_pgvector_ivfflat_backup` | IVFFlat index backup/restore |
| `test_pg_textsearch_bm25_backup_simple` | BM25 index + data backed up (SIMPLE) |
| `test_pg_textsearch_bm25_restore_simple` | Search works after SIMPLE restore (index rebuild) |
| `test_pg_textsearch_bm25_backup_full` | BM25 segment files captured in FULL mode |
| `test_pg_textsearch_bm25_restore_full` | BM25 search works after FULL restore (no rebuild) |
| `test_pgdog_backup_through_proxy` | Backup works when connected via pgdog proxy |
| `test_multiple_extensions_combined` | DB with pgvector + pg_textsearch backs up + restores |

---

## Extension Compatibility

### Compatibility Summary

| Extension | Storage | Custom WAL rmgr | Custom Table AM | Backup Impact |
|-----------|---------|----------------|-----------------|---------------|
| **TimescaleDB** | Standard heap tables (chunks in `_timescaledb_internal`) | No | Optional hypercore (columnstore) | Track extension version; preserve `_timescaledb_internal` catalogs |
| **pgvector** | Standard heap + custom index AMs (HNSW, IVFFlat) | No | No | Standard — indexes as relation files |
| **pg_textsearch** | Standard heap + custom BM25 index AM (LSM segments) | No (uses Generic XLOG) | No | Standard — segments as relation files; memtable ephemeral |
| **pgdog** | External proxy (no DB state) | N/A | N/A | No impact — stateless proxy |

### Why These All Work

All four use **standard PostgreSQL heap tables** for data and **standard relation files** for indexes. No custom WAL resource managers. This means:

- **FULL mode**: `copydir()` of `base/{dboid}/` captures everything — data, indexes, segments
- **SIMPLE mode**: `COPY ... TO` exports all table data; DDL recreates tables, indexes rebuild on restore
- **WAL filter**: all WAL records use standard rmgr IDs (RM_HEAP_ID, RM_BTREE_ID, RM_GENERIC_ID for pg_textsearch), filtered correctly by `dbOid`

### TimescaleDB Special Handling

TimescaleDB needs extra care in metadata:
1. **Extension version tracking**: `metadata.sql` must include exact version (`CREATE EXTENSION timescaledb VERSION 'x.y.z'`)
2. **`shared_preload_libraries`**: document that TimescaleDB must be preloaded on restore target
3. **Continuous aggregates**: WAL-based invalidation log is in standard tables — backed up normally
4. **Compressed chunks**: stored as standard heap relations — backed up normally
5. **Background jobs**: `_timescaledb_internal.bgw_job_stat` is a regular table — backed up

### pgvector Notes

- Vector data in standard heap columns — COPY binary handles `vector` type natively (registered I/O functions)
- HNSW/IVFFlat indexes: FULL mode preserves index files (no rebuild needed); SIMPLE mode requires `CREATE INDEX` on restore

### pg_textsearch Notes

- BM25 index uses LSM-tree with segments stored as relation files — FULL mode captures all segments
- In-memory memtable (DSA) is ephemeral — rebuilt from heap on startup after restore
- SIMPLE mode: index must be rebuilt via `CREATE INDEX` (DDL in SCHEMA section)
- Generic XLOG usage means WAL records use `RM_GENERIC_ID` — captured by WAL filter (block refs have correct `dbOid`)

### pgdog Notes

- External connection pooler written in Rust — no database state
- Backup connects directly to PostgreSQL, not through proxy
- No compatibility concerns

---

## Implementation Phases

### Phase 1: Scaffold + .bak Format
- `pg_dbbackup.control`, `Makefile`, SQL script, `src/pg_dbbackup.c` stubs
- `src/bakfile.c`: .bak writer/reader (magic, header, sections, footer)
- Config table + `set_mode/get_mode`
- **Tests**: Extension loads, .bak roundtrip, mode config

### Phase 2: Compression + Encryption
- `src/bakfile_crypto.c`: zstd compression + AES-256-GCM encryption per section
- **Tests**: compress roundtrip, encrypt roundtrip, wrong password, combined

### Phase 3: Metadata + DDL Generation
- `src/metadata_gen.c`: extensions, grants, ACLs → SQL
- `src/ddl_gen.c`: tables, indexes, types, functions → DDL (dependency-ordered)
- **Tests**: metadata covers all objects, DDL ordering correct

### Phase 4: SIMPLE Full Backup
- `src/backup_simple.c`: REPEATABLE READ → SCHEMA + DATA sections → .bak
- **Tests**: `test_simple_full_bak`, `test_simple_full_all_tables`

### Phase 5: SIMPLE Restore
- `src/restore_simple.c`: read .bak → temp DB → DDL → COPY FROM → metadata → rename
- **Tests**: `test_simple_restore_basic`, `test_simple_restore_replaces`

### Phase 6: SIMPLE Differential
- Change detection (WAL summaries / mtime)
- DIFF .bak: changed tables only
- Restore applies FULL + DIFF
- **Tests**: `test_simple_diff_only_changed`, `test_simple_restore_with_diff`

### Phase 7: FULL Full Backup
- `src/backup_full.c`: `do_pg_backup_start` → DATA + WAL sections → .bak
- **Tests**: `test_full_backup_bak`, `test_full_backup_concurrent`

### Phase 8: WAL Filter
- `src/wal_filter.c`: XLogReader + filter → .bak WAL section
- **Tests**: `test_wal_filter_includes_db`, `test_wal_filter_excludes_other`

### Phase 9: FULL Restore + PITR
- `src/restore_full.c`: temp DB → file inject → WAL replay → OID remap → PITR
- **Tests**: `test_full_restore_basic`, `test_pitr_stops_correctly`

### Phase 10: FULL Differential + Log
- DIFF .bak: changed blocks + WAL
- LOG .bak: WAL only
- Chain resolution in restore
- **Tests**: `test_full_diff_smaller`, `test_full_restore_chain`, `test_pitr_across_logs`

### Phase 11: Inspection Functions
- `pg_dbbackup_header()`, `pg_dbbackup_filelist()`, `pg_dbbackup_verify()` SRFs
- **Tests**: inspection + verification tests

### Phase 12: Extension Compatibility Testing
- TimescaleDB: hypertable backup/restore, continuous aggregates, compressed chunks, PITR
- pgvector: vector data, HNSW/IVFFlat index backup/restore, similarity search verification
- pg_textsearch: BM25 index segments, search after restore, rebuild in SIMPLE mode
- pgdog: backup through proxy connection
- Combined: DB with multiple extensions
- **Tests**: all `test_timescaledb_*`, `test_pgvector_*`, `test_pg_textsearch_*`, `test_pgdog_*`, `test_multi_extension_*`

Hint: Use https://hub.docker.com/r/gerardsmit/pg_textsearch - all the extensions are preinstalled and ready to use for testing.

### Phase 13: Polish
- Progress via NOTICE messages
- Async variant with background worker
- Documentation

---

## Prerequisites

- `wal_level = replica` (or `logical`)
- Superuser or `pg_write_server_files` role
- PG15+ (modern backup APIs, XLogReader)
- `track_commit_timestamp = on` (recommended for PITR precision)
- OpenSSL (for AES-256-GCM encryption — PG already links it)

---

## Risks and Mitigations

| Risk | Mitigation |
|------|-----------|
| WAL recycled before reading | Temp replication slot pins WAL |
| OID remapping edge cases | Include all global rmgrs; integration tests |
| DDL generation misses cases | Compare vs pg_dump; iterate |
| .bak format versioning | Version field in header; reader validates |
| Large .bak (>2GB) | 64-bit lengths; streaming write |
| Rename needs zero connections | Temp name, rename at end |
| OpenSSL not available | Encryption optional; error if password given without OpenSSL |

---

## Key PostgreSQL Source References

| What | File |
|------|------|
| `do_pg_backup_start/stop` | `src/backend/access/transam/xlog.c` |
| XLogReader API | `src/include/access/xlogreader.h` |
| WAL record format | `src/include/access/xlogrecord.h` |
| Resource manager IDs | `src/include/access/rmgrlist.h` |
| `RelFileLocator` (dbOid) | `src/include/storage/relfilelocator.h` |
| `copydir()` | `src/include/storage/copydir.h` |
| Base backup reference | `src/backend/backup/basebackup.c` |
| CREATE DATABASE | `src/backend/commands/dbcommands.c` |
| RenameDatabase | `src/backend/commands/dbcommands.c:1918` |
| pg_waldump XLogReader | `src/bin/pg_waldump/pg_waldump.c` |
| pg_dump DDL/extensions | `src/bin/pg_dump/pg_dump.c` |
| COPY binary format | `src/backend/commands/copyfromparse.c` |
| SPI interface | `src/backend/executor/spi.c` |
| OpenSSL in PG | `src/common/cryptohash_openssl.c` |
