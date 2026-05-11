#ifndef PG_DBBACKUP_LIBPQ_HELPERS_H
#define PG_DBBACKUP_LIBPQ_HELPERS_H

#include "postgres.h"

#include <libpq-fe.h>

extern PGconn *pgbu_connect_libpq(const char *dbname);
extern PGresult *pgbu_libpq_exec_interruptible(PGconn *conn, const char *sql);
extern void pgbu_libpq_exec(PGconn *conn, const char *sql, const char *what);
extern void pgbu_libpq_exec_schema_idempotent(PGconn *conn, const char *script);
extern void pgbu_drop_temp_db_quiet(const char *temp_dbname);

extern char *pgbu_generate_temp_dbname(void);
extern char *pgbu_quote_db_identifier(const char *name);

#endif							/* PG_DBBACKUP_LIBPQ_HELPERS_H */
