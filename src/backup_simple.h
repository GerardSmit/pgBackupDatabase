#ifndef BACKUP_SIMPLE_H
#define BACKUP_SIMPLE_H

#include "postgres.h"
#include "pg_dbbackup.h"
#include "bakfile.h"

extern void backup_simple_full(Oid db_oid, const char *db_name,
								const char *filepath, bool compress,
								const char *password);
extern void backup_simple_full_as_mode(Oid db_oid, const char *db_name,
										const char *filepath, bool compress,
										const char *password,
										PgDbBackupMode mode);
extern void backup_simple_full_as_mode_lsn(Oid db_oid, const char *db_name,
										   const char *filepath,
										   bool compress,
										   const char *password,
										   PgDbBackupMode mode,
										   XLogRecPtr chain_lsn);
extern void backup_simple_differential(Oid db_oid, const char *db_name,
										const char *filepath,
										const char *base_filepath,
										bool compress, const char *password);

#endif
