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

#endif /* PG_DBBACKUP_H */
