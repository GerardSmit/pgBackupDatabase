#ifndef RESTORE_FULL_H
#define RESTORE_FULL_H

#include "postgres.h"
#include "pg_dbbackup.h"
#include "utils/timestamp.h"

extern void restore_full(const char *target_db, char **files, int file_count,
						  TimestampTz stop_at, bool has_stop_at,
						  const char *password);

#endif
