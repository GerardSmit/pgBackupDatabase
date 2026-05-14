#ifndef PG_DBBACKUP_LIBPQ_HELPERS_H
#define PG_DBBACKUP_LIBPQ_HELPERS_H

#include "postgres.h"

#include <libpq-fe.h>

extern PGconn *pgbu_connect_libpq(const char *dbname);
extern PGconn *pgbu_connect_libpq_conninfo(const char *conninfo);
extern PGresult *pgbu_libpq_exec_interruptible(PGconn *conn, const char *sql);
extern PGresult *pgbu_libpq_exec_params_interruptible(PGconn *conn,
													  const char *sql,
													  int nparams,
													  const Oid *param_types,
													  const char *const *param_values);
extern void pgbu_libpq_exec(PGconn *conn, const char *sql, const char *what);
extern void pgbu_libpq_exec_schema_idempotent(PGconn *conn, const char *script);
extern void pgbu_drop_temp_db_quiet(const char *temp_dbname);

extern char *pgbu_generate_temp_dbname(void);
extern char *pgbu_quote_db_identifier(const char *name);

/*
 * Return a palloc'd quoted column list for the given relation, suitable
 * for splicing into a COPY/INSERT/SELECT statement. Used by both the
 * SIMPLE and FULL restore paths to discover the live target schema.
 */
extern char *pgbu_libpq_copy_column_list(PGconn *conn, const char *schema,
										  const char *relname,
										  const char *path);

#endif							/* PG_DBBACKUP_LIBPQ_HELPERS_H */
