# pg_dbbackup v1 FULL-Mode Support Matrix

FULL mode is per-database logical PITR. A feature is allowed only when it can be
represented in the selected database's logical backup chain. If it cannot, the
backup must fail before writing a misleading `.bak`.

## Supported And Tested

| Feature | Backup behavior | Test coverage |
|---|---|---|
| Ordinary logged tables | Binary COPY in FULL, logical DML in LOG/DIFF | Existing FULL restore tests |
| Primary-key or `REPLICA IDENTITY FULL` DML | `INSERT`, `UPDATE`, `DELETE`, `TRUNCATE` replay transactionally | Existing FULL LOG/PITR tests |
| DDL during LOG windows | Event-trigger journal replayed in commit order | `FullRestore_Replays_Ddl_Created_During_Log_Window` |
| Sequences and identity columns | FULL state plus LOG state frames, `setval` at restore stop point | sequence PITR tests, `FullRestore_RoundTrips_Rls_Identity_Generated_Domain_And_Enum` |
| Generated columns | COPY/logical replay omit generated columns and let PostgreSQL recalculate | `FullRestore_RoundTrips_Rls_Identity_Generated_Domain_And_Enum` |
| Enums and domains | DDL generator emits enum labels and domain constraints | `FullRestore_RoundTrips_Rls_Identity_Generated_Domain_And_Enum` |
| Row-level security | RLS flags and policies are emitted after tables/views/functions | `FullRestore_RoundTrips_Rls_Identity_Generated_Domain_And_Enum` |
| User triggers | Trigger DDL is emitted; FULL load and LOG replay run with `session_replication_role = replica` | `FullRestore_Replays_TriggerSideEffects_Without_DoubleFiring` |
| Views and rewrite rules | View and user rule definitions are emitted after dependencies | `FullRestore_RoundTrips_Views_And_Rewrite_Rules` |
| Declarative partitioning | Partitioned roots/children and post-FULL partition DDL are replayed | `FullRestore_RoundTrips_Partitioned_Table_And_PostFull_Partition_Ddl` |
| Large objects | FULL snapshot plus LOG create/write/unlink snapshots | `FullRestore_Restores_LargeObjects_From_Full_And_Log`, PITR LO test |
| Missing roles on restore target | Restore warns and creates NOLOGIN placeholder roles | ownership/grants test |
| Fresh PostgreSQL installation restore | Chain restores into a second container | `CrossContainerRestoreTests` |
| HA primary failover readiness | Chain slots and restore continuation slots are created with `failover = true`; readiness/status functions expose PostgreSQL's synced persistent standby state; LOG/DIFF rejects missing, temporary, invalidated, or non-failover chain slots | failover slot tests |
| PgDog primary routing | Backup succeeds through a PgDog route that targets the primary; direct or PgDog standby routes fail instead of rerouting silently | `PgdogTests` |
| TimescaleDB hypertables | Hypertable metadata, chunk DML remapping, dimensions, compression, policies, CAGGs | `TimescaleDbTests` |

## Rejected Before Backup

These objects are not silently omitted. FULL, DIFFERENTIAL, and LOG backups
raise `feature_not_supported` before advancing the chain.

| Rejected feature | Reason | Test coverage |
|---|---|---|
| Unlogged tables | Changes are not WAL/logically durable | `FullBackup_Rejects_Unlogged_Tables` |
| Foreign tables | External data is outside the selected database backup | `FullBackup_Rejects_Foreign_Tables` |
| Regular materialized views | Refresh state is physical/derived and not yet represented as a PITR-safe frame | `FullBackup_Rejects_Regular_Materialized_Views` |
| Ordinary table inheritance | Parent/child COPY and replay semantics differ from declarative partitions | `FullBackup_Rejects_Ordinary_Table_Inheritance` |
| User-defined event triggers | They affect restore-time DDL semantics and need a dedicated adapter | `FullBackup_Rejects_User_Event_Triggers` |
| Custom range/base/pseudo types | DDL/behavior is not yet fully represented by v1 logical frames | `FullBackup_Rejects_Custom_Range_Types` |
| Custom text search configurations | Parser/dictionary/config dependencies are not yet fully represented by v1 logical frames | `FullBackup_Rejects_Custom_Text_Search_Configurations` |
| User aggregates | Aggregate transition/final/combine behavior needs dedicated DDL support | `FullBackup_Rejects_User_Aggregates` |
| Logical publications/subscriptions | Replication topology is cluster/external state, not database data | `FullBackup_Rejects_Logical_Publications`, `FullBackup_Rejects_Logical_Subscriptions` |

## Required Runtime Settings

- `shared_preload_libraries` must include `pg_dbbackup`.
- `wal_level` must be `logical`.
- PostgreSQL 17+ failover logical slots are used for FULL-mode chains. Seamless
  continuation after promotion also requires PostgreSQL slot synchronization
  configuration on the primary/standby pair and
  `dbbackup.pg_dbbackup_failover_slot_ready('<db>') = true` on the candidate
  standby before promotion.
- PgDog or another proxy must route backup jobs to the current primary.
  Standby routes are rejected; the extension does not forward them internally.
- FULL-mode tables must have a primary key or `REPLICA IDENTITY FULL`.
- The restore role must be superuser because restore temporarily sets
  `session_replication_role = replica`.
