#ifndef RESTORE_SIMPLE_H
#define RESTORE_SIMPLE_H

#include "postgres.h"
#include "pg_dbbackup.h"

extern void restore_simple(const char *target_db, char **files, int file_count,
							const char *password);

#endif
