#ifndef BACKUP_ASYNC_H
#define BACKUP_ASYNC_H

#include "postgres.h"
#include "fmgr.h"

extern Datum pg_dbbackup_async(PG_FUNCTION_ARGS);
extern Datum pg_dbbackup_wait(PG_FUNCTION_ARGS);

extern PGDLLEXPORT void pgbu_async_worker_main(Datum main_arg);

#endif
