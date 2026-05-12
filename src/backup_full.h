#ifndef BACKUP_FULL_H
#define BACKUP_FULL_H

#include "postgres.h"
#include "access/xlogdefs.h"
#include "pg_dbbackup.h"
#include "bakfile.h"

typedef struct PgDbBackupDeferredAdvance
{
	char	   *slot_name;
	XLogRecPtr	stop_lsn;
	bool		reset_journal;
	bool		drop_slot_on_abort;
} PgDbBackupDeferredAdvance;

extern void backup_full_full(Oid db_oid, const char *db_name,
							  const char *filepath, bool compress,
							  const char *password);
extern void backup_full_full_deferred(Oid db_oid, const char *db_name,
									  const char *filepath, bool compress,
									  const char *password,
									  PgDbBackupDeferredAdvance *advance);
extern void backup_full_differential(Oid db_oid, const char *db_name,
									  const char *filepath,
									  const char *base_filepath,
									  bool compress, const char *password);
extern void backup_full_differential_deferred(Oid db_oid, const char *db_name,
											  const char *filepath,
											  const char *base_filepath,
											  bool compress,
											  const char *password,
											  PgDbBackupDeferredAdvance *advance);
extern void backup_full_log(Oid db_oid, const char *db_name,
							 const char *filepath, const char *prev_filepath,
							 bool compress, const char *password);
extern void backup_full_log_deferred(Oid db_oid, const char *db_name,
									 const char *filepath,
									 const char *prev_filepath,
									 bool compress, const char *password,
									 PgDbBackupDeferredAdvance *advance);
extern void backup_full_finish_deferred_advance(Oid db_oid,
												const char *db_name,
												PgDbBackupDeferredAdvance *advance);
extern void backup_full_abort_deferred_advance(PgDbBackupDeferredAdvance *advance);
extern void backup_full_free_deferred_advance(PgDbBackupDeferredAdvance *advance);

#endif
