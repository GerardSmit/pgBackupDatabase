#ifndef PG_DBBACKUP_H
#define PG_DBBACKUP_H

#include "postgres.h"
#include "fmgr.h"

#define PGDBBACKUP_SCHEMA "dbbackup"

typedef enum PgDbBackupMode
{
	BACKUP_MODE_SIMPLE,
	BACKUP_MODE_FULL
} PgDbBackupMode;

typedef enum PgDbBackupType
{
	BACKUP_TYPE_FULL,
	BACKUP_TYPE_DIFFERENTIAL,
	BACKUP_TYPE_LOG
} PgDbBackupType;

extern PgDbBackupMode pg_dbbackup_resolve_mode(Oid db_oid);
extern bool pgdb_scheduler_enabled;
extern char *pgdb_scheduler_database;
extern int pgdb_scheduler_interval_ms;

/*
 * Defaults applied to PgdbS3Config when a storage_targets row leaves the
 * matching column NULL. Exposed as GUCs so an operator can retune timeouts
 * and retry budgets without rewriting catalog rows.
 */
extern int pgdb_s3_default_max_retries;
extern int pgdb_s3_default_connect_timeout_ms;
extern int pgdb_s3_default_request_timeout_ms;
extern int pgdb_s3_default_bandwidth_limit_kbps;

/* Parse a 'full'/'differential'/'log' string into PgDbBackupType. */
extern PgDbBackupType pgdb_parse_backup_type(const char *type_str);

/*
 * Auto-route a backup request from a hot standby to the primary.
 *
 * Returns false when the current server is the primary, telling the caller to
 * proceed with the local backup path. Returns true (and fills *out_result)
 * after successfully re-invoking the call on the primary via libpq using the
 * built-in `primary_conninfo` GUC.
 *
 * Raises ereport(ERROR) when:
 *   - the call is for the local-path entry (pg_dbbackup) on a standby —
 *     the .bak would land on the primary's filesystem, surprising callers;
 *   - the recursion-guard GUC `dbbackup.in_remote_invocation` is on but the
 *     server is still in recovery (i.e. routing chain misconfigured);
 *   - `primary_conninfo` is empty on the standby;
 *   - the libpq connection or the routed call itself fails.
 *
 * Only valid for entries whose first argument is the database name (text) and
 * whose remaining arguments match dbbackup.pg_dbbackup_to_storage's signature.
 */
extern bool pgdb_route_to_primary_if_standby(FunctionCallInfo fcinfo,
											  bool local_path_entry,
											  Datum *out_result);

/*
 * Reject obviously dangerous filesystem paths from SQL callers: NUL bytes,
 * embedded '..' segments, or empty strings. Callers retain the right to
 * impose stricter checks; this is a baseline that closes the trivial
 * traversal and embedded-NUL footguns even though only superusers can
 * invoke the surface functions.
 */
extern void pgdb_validate_filesystem_path(const char *path,
										   const char *what);

/*
 * Schema-namespace filter shared by the SQL string builders that need to
 * exclude pg_catalog, information_schema, our own dbbackup schema, and
 * TimescaleDB internals. Two variants exist because some call sites embed
 * the snippet into appendStringInfo (where '%' must be doubled) while
 * others pass it straight to SPI_execute.
 */
#define PGDBBACKUP_SKIP_SYSTEM_NSP \
	"n.nspname NOT LIKE 'pg\\_%' " \
	"AND n.nspname NOT IN ('information_schema', 'dbbackup', " \
	"'_timescaledb_internal', '_timescaledb_catalog', " \
	"'_timescaledb_config', '_timescaledb_cache', " \
	"'_timescaledb_functions', 'timescaledb_information', " \
	"'timescaledb_experimental')"
#define PGDBBACKUP_SKIP_SYSTEM_NSP_FMT \
	"n.nspname NOT LIKE 'pg\\_%%' " \
	"AND n.nspname NOT IN ('information_schema', 'dbbackup', " \
	"'_timescaledb_internal', '_timescaledb_catalog', " \
	"'_timescaledb_config', '_timescaledb_cache', " \
	"'_timescaledb_functions', 'timescaledb_information', " \
	"'timescaledb_experimental')"

#endif /* PG_DBBACKUP_H */
