#ifndef BACKUP_FULL_H
#define BACKUP_FULL_H

#include "postgres.h"
#include "pg_dbbackup.h"
#include "bakfile.h"

extern void backup_full_full(Oid db_oid, const char *db_name,
							  const char *filepath, bool compress,
							  const char *password);
extern void backup_full_differential(Oid db_oid, const char *db_name,
									  const char *filepath,
									  const char *base_filepath,
									  bool compress, const char *password);
extern void backup_full_log(Oid db_oid, const char *db_name,
							 const char *filepath, const char *prev_filepath,
							 bool compress, const char *password);

#endif
