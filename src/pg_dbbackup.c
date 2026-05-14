#include "postgres.h"

#include <libpq-fe.h>

#include "access/xlog.h"
#include "catalog/namespace.h"
#include "catalog/pg_database.h"
#include "catalog/pg_type.h"
#include "commands/dbcommands.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "lib/stringinfo.h"
#include "miscadmin.h"
#include "postmaster/bgworker.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/guc.h"
#include "utils/lsyscache.h"
#include "utils/syscache.h"
#include "utils/uuid.h"

#include "pg_dbbackup.h"
#include "bakfile.h"
#include "bakfile_crypto.h"
#include "backup_simple.h"
#include "backup_full.h"
#include "restore_simple.h"
#include "restore_full.h"
#include "inspect.h"
#include "libpq_helpers.h"
#include "logical_journal.h"
#include "metadata_gen.h"
#include "ddl_gen.h"

PG_MODULE_MAGIC;

void _PG_init(void);
void pgbu_scheduler_worker_main(Datum main_arg);

bool		pgdb_scheduler_enabled = true;
char	   *pgdb_scheduler_database = NULL;
int			pgdb_scheduler_interval_ms = 60000;

int			pgdb_s3_default_max_retries = 3;
int			pgdb_s3_default_connect_timeout_ms = 10000;
int			pgdb_s3_default_request_timeout_ms = 300000;
int			pgdb_s3_default_bandwidth_limit_kbps = 0;

/*
 * Marker set on the routed connection so the primary's pg_dbbackup_to_storage
 * does not re-route. Carried via libpq `options='-c dbbackup.in_remote_invocation=on'`.
 */
static bool pgdb_in_remote_invocation = false;

PG_FUNCTION_INFO_V1(pg_dbbackup_set_mode);
PG_FUNCTION_INFO_V1(pg_dbbackup_get_mode);
PG_FUNCTION_INFO_V1(pg_dbbackup);
PG_FUNCTION_INFO_V1(pg_dbrestore);
PG_FUNCTION_INFO_V1(pg_dbbackup_header);
PG_FUNCTION_INFO_V1(pg_dbbackup_filelist);
PG_FUNCTION_INFO_V1(pg_dbbackup_verify);
PG_FUNCTION_INFO_V1(pg_dbbackup_test_crypto);
PG_FUNCTION_INFO_V1(pg_dbbackup_test_metadata);
PG_FUNCTION_INFO_V1(pg_dbbackup_test_ddl);

void
_PG_init(void)
{
	DefineCustomBoolVariable(
		"dbbackup.scheduler_enabled",
		"Runs the pg_dbbackup schedule dispatcher background worker.",
		NULL,
		&pgdb_scheduler_enabled,
		true,
		PGC_POSTMASTER,
		0,
		NULL,
		NULL,
		NULL);

	DefineCustomStringVariable(
		"dbbackup.scheduler_database",
		"Database containing pg_dbbackup storage targets, backup sets, and schedules.",
		NULL,
		&pgdb_scheduler_database,
		"postgres",
		PGC_POSTMASTER,
		0,
		NULL,
		NULL,
		NULL);

	DefineCustomIntVariable(
		"dbbackup.scheduler_interval_ms",
		"Interval between pg_dbbackup scheduler wakeups.",
		NULL,
		&pgdb_scheduler_interval_ms,
		60000,
		1000,
		3600000,
		PGC_SIGHUP,
		0,
		NULL,
		NULL,
		NULL);

	DefineCustomIntVariable(
		"dbbackup.s3_default_max_retries",
		"Default S3 retry budget when storage_targets.max_retries is NULL.",
		NULL,
		&pgdb_s3_default_max_retries,
		3,
		0,
		20,
		PGC_SIGHUP,
		0,
		NULL,
		NULL,
		NULL);

	DefineCustomIntVariable(
		"dbbackup.s3_default_connect_timeout_ms",
		"Default S3 connect timeout (ms) when storage_targets value is NULL.",
		NULL,
		&pgdb_s3_default_connect_timeout_ms,
		10000,
		1000,
		600000,
		PGC_SIGHUP,
		0,
		NULL,
		NULL,
		NULL);

	DefineCustomIntVariable(
		"dbbackup.s3_default_request_timeout_ms",
		"Default S3 per-request timeout (ms) when storage_targets value is NULL.",
		NULL,
		&pgdb_s3_default_request_timeout_ms,
		300000,
		1000,
		3600000,
		PGC_SIGHUP,
		0,
		NULL,
		NULL,
		NULL);

	DefineCustomIntVariable(
		"dbbackup.s3_default_bandwidth_limit_kbps",
		"Default S3 outbound bandwidth ceiling (KiB/s); 0 disables throttling.",
		NULL,
		&pgdb_s3_default_bandwidth_limit_kbps,
		0,
		0,
		PG_INT32_MAX,
		PGC_SIGHUP,
		0,
		NULL,
		NULL,
		NULL);

	DefineCustomBoolVariable(
		"dbbackup.in_remote_invocation",
		"Set on a primary by pg_dbbackup auto-routing so the called backup "
		"does not attempt to route again.",
		NULL,
		&pgdb_in_remote_invocation,
		false,
		PGC_USERSET,
		0,
		NULL,
		NULL,
		NULL);

	pgdb_logical_journal_init(process_shared_preload_libraries_in_progress);

	if (process_shared_preload_libraries_in_progress &&
		pgdb_scheduler_enabled)
	{
		BackgroundWorker worker;

		memset(&worker, 0, sizeof(worker));
		worker.bgw_flags = BGWORKER_SHMEM_ACCESS |
			BGWORKER_BACKEND_DATABASE_CONNECTION;
		worker.bgw_start_time = BgWorkerStart_RecoveryFinished;
		worker.bgw_restart_time = 60;
		strlcpy(worker.bgw_library_name, "pg_dbbackup", BGW_MAXLEN);
		strlcpy(worker.bgw_function_name, "pgbu_scheduler_worker_main",
				BGW_MAXLEN);
		snprintf(worker.bgw_name, BGW_MAXLEN, "pg_dbbackup scheduler");
		snprintf(worker.bgw_type, BGW_MAXLEN, "pg_dbbackup");
		RegisterBackgroundWorker(&worker);
	}
}

/*
 * Resolve the backup mode for a database by querying pg_dbbackup.db_config.
 * Returns BACKUP_MODE_SIMPLE if no row exists (default).
 */
PgDbBackupMode
pg_dbbackup_resolve_mode(Oid db_oid)
{
	PgDbBackupMode mode = BACKUP_MODE_SIMPLE;
	int			ret;

	SPI_connect();
	ret = SPI_execute_with_args(
		"SELECT mode FROM dbbackup.db_config WHERE db_oid = $1",
		1,
		(Oid[]){OIDOID},
		(Datum[]){ObjectIdGetDatum(db_oid)},
		NULL, true, 1);

	if (ret == SPI_OK_SELECT && SPI_processed > 0)
	{
		char	   *mode_str = SPI_getvalue(SPI_tuptable->vals[0],
											SPI_tuptable->tupdesc, 1);

		if (mode_str && strcmp(mode_str, "full") == 0)
			mode = BACKUP_MODE_FULL;
	}

	SPI_finish();
	return mode;
}

static Oid
resolve_db_oid(const char *dbname)
{
	Oid			db_oid;
	HeapTuple	tuple;

	tuple = SearchSysCache1(DATABASEOID,
							CStringGetDatum(dbname));
	if (!HeapTupleIsValid(tuple))
	{
		/* try by name */
		db_oid = get_database_oid(dbname, false);
	}
	else
	{
		db_oid = ((Form_pg_database) GETSTRUCT(tuple))->oid;
		ReleaseSysCache(tuple);
	}

	return db_oid;
}

/*
 * Append `KEY='VALUE'` to a libpq conninfo string, escaping single quotes
 * and backslashes inside VALUE so the conninfo parser sees a single token.
 */
static void
append_conninfo_quoted(StringInfo out, const char *key, const char *value)
{
	const char *p;

	appendStringInfo(out, " %s='", key);
	for (p = value; *p; p++)
	{
		if (*p == '\'' || *p == '\\')
			appendStringInfoChar(out, '\\');
		appendStringInfoChar(out, *p);
	}
	appendStringInfoChar(out, '\'');
}

/*
 * Translate a libpq SQLSTATE string ("25006") into the int form used by
 * errcode(). Falls back to ERRCODE_INTERNAL_ERROR for malformed input so we
 * never lose the underlying error message, just the precise code.
 */
static int
parse_libpq_sqlstate(const char *sqlstate)
{
	if (sqlstate == NULL || strlen(sqlstate) != 5)
		return ERRCODE_INTERNAL_ERROR;
	return MAKE_SQLSTATE(sqlstate[0], sqlstate[1], sqlstate[2],
						 sqlstate[3], sqlstate[4]);
}

/*
 * Re-invoke pg_dbbackup_to_storage on the primary via libpq. fcinfo points at
 * the in-recovery caller's arguments; the rerouted call receives them
 * verbatim, with a recursion-guard GUC set on the new connection so the
 * primary's pg_dbbackup_to_storage will not try to route in turn.
 *
 * The returned Datum is the routed backup_id (uuid).
 */
static Datum
route_storage_backup_to_primary(FunctionCallInfo fcinfo)
{
	const char *primary_conninfo;
	char	   *dbname;
	char	   *type_str;
	char	   *target_str;
	char	   *backup_set_str;
	char	   *password_str;
	char	   *base_id_str;
	bool		compress;
	StringInfoData ci;
	PGconn	   *conn = NULL;
	PGresult   *res = NULL;
	Datum		result = (Datum) 0;
	const char *values[7];
	Oid			types[7] = {TEXTOID, TEXTOID, TEXTOID, TEXTOID,
							BOOLOID, TEXTOID, UUIDOID};

	primary_conninfo = GetConfigOption("primary_conninfo", true, false);
	if (primary_conninfo == NULL || primary_conninfo[0] == '\0')
		ereport(ERROR,
				(errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
				 errmsg("pg_dbbackup auto-route: primary_conninfo is empty on this standby"),
				 errhint("Set primary_conninfo so the extension can reach the primary, "
						 "or invoke pg_dbbackup_to_storage() directly on the primary.")));

	dbname = text_to_cstring(PG_GETARG_TEXT_PP(0));
	type_str = text_to_cstring(PG_GETARG_TEXT_PP(1));
	target_str = PG_ARGISNULL(2) ? NULL :
		text_to_cstring(PG_GETARG_TEXT_PP(2));
	backup_set_str = PG_ARGISNULL(3) ? NULL :
		text_to_cstring(PG_GETARG_TEXT_PP(3));
	compress = PG_GETARG_BOOL(4);
	password_str = PG_ARGISNULL(5) ? NULL :
		text_to_cstring(PG_GETARG_TEXT_PP(5));
	base_id_str = PG_ARGISNULL(6) ? NULL :
		DatumGetCString(DirectFunctionCall1(uuid_out, PG_GETARG_DATUM(6)));

	values[0] = dbname;
	values[1] = type_str;
	values[2] = target_str;
	values[3] = backup_set_str;
	values[4] = compress ? "t" : "f";
	values[5] = password_str;
	values[6] = base_id_str;

	initStringInfo(&ci);
	appendStringInfoString(&ci, primary_conninfo);
	append_conninfo_quoted(&ci, "dbname", dbname);
	append_conninfo_quoted(&ci, "options",
						   "-c dbbackup.in_remote_invocation=on");
	append_conninfo_quoted(&ci, "application_name", "pg_dbbackup_auto_route");

	PG_TRY();
	{
		conn = pgbu_connect_libpq_conninfo(ci.data);

		res = pgbu_libpq_exec_params_interruptible(
			conn,
			"SELECT dbbackup.pg_dbbackup_to_storage($1,$2,$3,$4,$5,$6,$7)",
			7, types, values);

		if (res == NULL)
		{
			char	   *msg = pstrdup(PQerrorMessage(conn));

			ereport(ERROR,
					(errcode(ERRCODE_CONNECTION_FAILURE),
					 errmsg("pg_dbbackup auto-route: lost connection to primary"),
					 errdetail("%s", msg)));
		}

		if (PQresultStatus(res) != PGRES_TUPLES_OK)
		{
			const char *primary_field = PQresultErrorField(res, PG_DIAG_MESSAGE_PRIMARY);
			const char *detail_field = PQresultErrorField(res, PG_DIAG_MESSAGE_DETAIL);
			const char *hint_field = PQresultErrorField(res, PG_DIAG_MESSAGE_HINT);
			const char *sqlstate = PQresultErrorField(res, PG_DIAG_SQLSTATE);
			int			code = parse_libpq_sqlstate(sqlstate);
			char	   *primary_msg = pstrdup(primary_field ? primary_field
												  : "pg_dbbackup auto-route to primary failed");
			char	   *detail_msg = detail_field ? pstrdup(detail_field) : NULL;
			char	   *hint_msg = hint_field ? pstrdup(hint_field) : NULL;

			/*
			 * Re-raise the upstream error with its original SQLSTATE and
			 * primary message so callers see exactly what the primary saw,
			 * not a wrapped "auto-route failed" placeholder. Detail/hint are
			 * forwarded too when the primary set them.
			 */
			ereport(ERROR,
					(errcode(code),
					 errmsg_internal("%s", primary_msg),
					 detail_msg != NULL ? errdetail_internal("%s", detail_msg) : 0,
					 hint_msg != NULL ? errhint("%s", hint_msg) : 0,
					 errcontext("pg_dbbackup auto-route from standby to primary")));
		}

		if (PQntuples(res) != 1 || PQnfields(res) != 1 ||
			PQgetisnull(res, 0, 0))
			ereport(ERROR,
					(errcode(ERRCODE_INTERNAL_ERROR),
					 errmsg("pg_dbbackup auto-route returned an unexpected result shape"),
					 errdetail("Expected one row with one non-NULL uuid column.")));

		{
			char	   *uuid_text = PQgetvalue(res, 0, 0);

			result = DirectFunctionCall1(uuid_in,
										 CStringGetDatum(uuid_text));
		}
	}
	PG_FINALLY();
	{
		if (res != NULL)
			PQclear(res);
		if (conn != NULL)
			PQfinish(conn);
	}
	PG_END_TRY();

	return result;
}

bool
pgdb_route_to_primary_if_standby(FunctionCallInfo fcinfo,
								  bool local_path_entry,
								  Datum *out_result)
{
	if (!RecoveryInProgress())
		return false;

	if (pgdb_in_remote_invocation)
		ereport(ERROR,
				(errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
				 errmsg("pg_dbbackup auto-route landed on a server still in recovery"),
				 errhint("primary_conninfo on the original standby points at another "
						 "standby. Fix the routing chain.")));

	if (local_path_entry)
		ereport(ERROR,
				(errcode(ERRCODE_READ_ONLY_SQL_TRANSACTION),
				 errmsg("pg_dbbackup() refuses to run on a hot standby because the "
						"output file would be written on the primary's filesystem"),
				 errhint("Use dbbackup.pg_dbbackup_to_storage() so the backup lands "
						 "in shared storage and is auto-routed through primary_conninfo, "
						 "or invoke pg_dbbackup() directly on the primary.")));

	*out_result = route_storage_backup_to_primary(fcinfo);
	return true;
}

Datum
pg_dbbackup_set_mode(PG_FUNCTION_ARGS)
{
	text	   *dbname_text = PG_GETARG_TEXT_PP(0);
	text	   *mode_text = PG_GETARG_TEXT_PP(1);
	char	   *dbname = text_to_cstring(dbname_text);
	char	   *mode = text_to_cstring(mode_text);
	Oid			db_oid;

	if (!superuser())
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
				 errmsg("must be superuser to set backup mode")));

	if (strcmp(mode, "simple") != 0 && strcmp(mode, "full") != 0)
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("mode must be 'simple' or 'full'")));

	db_oid = get_database_oid(dbname, false);

	SPI_connect();
	SPI_execute_with_args(
		"INSERT INTO dbbackup.db_config (db_oid, db_name, mode) "
		"VALUES ($1, $2, $3) "
		"ON CONFLICT (db_oid) DO UPDATE SET db_name = $2, mode = $3",
		3,
		(Oid[]){OIDOID, TEXTOID, TEXTOID},
		(Datum[]){ObjectIdGetDatum(db_oid),
				  CStringGetTextDatum(dbname),
				  CStringGetTextDatum(mode)},
		NULL, false, 0);
	SPI_finish();

	PG_RETURN_VOID();
}

Datum
pg_dbbackup_get_mode(PG_FUNCTION_ARGS)
{
	text	   *dbname_text = PG_GETARG_TEXT_PP(0);
	char	   *dbname = text_to_cstring(dbname_text);
	Oid			db_oid;
	PgDbBackupMode mode;

	db_oid = get_database_oid(dbname, false);
	mode = pg_dbbackup_resolve_mode(db_oid);

	PG_RETURN_TEXT_P(cstring_to_text(
		mode == BACKUP_MODE_FULL ? "full" : "simple"));
}

PgDbBackupType
pgdb_parse_backup_type(const char *type_str)
{
	if (type_str != NULL)
	{
		if (strcmp(type_str, "full") == 0)
			return BACKUP_TYPE_FULL;
		if (strcmp(type_str, "differential") == 0)
			return BACKUP_TYPE_DIFFERENTIAL;
		if (strcmp(type_str, "log") == 0)
			return BACKUP_TYPE_LOG;
	}

	ereport(ERROR,
			(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
			 errmsg("type must be 'full', 'differential', or 'log'")));
	return BACKUP_TYPE_FULL;		/* unreachable */
}

void
pgdb_validate_filesystem_path(const char *path, const char *what)
{
	const char *p;

	if (path == NULL || path[0] == '\0')
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("%s must not be empty", what)));

	for (p = path; *p; p++)
	{
		if (*p == '\0')
			ereport(ERROR,
					(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
					 errmsg("%s must not contain NUL bytes", what)));
	}

	/* Reject ../ and /.. segments anywhere in the path. */
	for (p = path; *p; p++)
	{
		bool		at_segment_start = (p == path) ||
			*(p - 1) == '/' || *(p - 1) == '\\';

		if (at_segment_start && p[0] == '.' && p[1] == '.' &&
			(p[2] == '\0' || p[2] == '/' || p[2] == '\\'))
			ereport(ERROR,
					(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
					 errmsg("%s must not contain \"..\" path segments",
							what),
					 errdetail("Got %s = \"%s\".", what, path)));
	}
}

Datum
pg_dbbackup(PG_FUNCTION_ARGS)
{
	text	   *dbname_text = PG_GETARG_TEXT_PP(0);
	text	   *filepath_text = PG_GETARG_TEXT_PP(1);
	text	   *type_text = PG_GETARG_TEXT_PP(2);
	bool		do_compress = PG_GETARG_BOOL(3);
	char	   *password = PG_ARGISNULL(4) ? NULL : text_to_cstring(PG_GETARG_TEXT_PP(4));
	char	   *base_filepath = PG_ARGISNULL(5) ? NULL : text_to_cstring(PG_GETARG_TEXT_PP(5));
	char	   *dbname = text_to_cstring(dbname_text);
	char	   *filepath = text_to_cstring(filepath_text);
	PgDbBackupType backup_type = pgdb_parse_backup_type(text_to_cstring(type_text));
	Oid			db_oid;
	PgDbBackupMode mode;

	if (!superuser())
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
				 errmsg("must be superuser to perform backups")));

	{
		Datum		routed_result;

		if (pgdb_route_to_primary_if_standby(fcinfo, true, &routed_result))
			PG_RETURN_DATUM(routed_result);	/* unreachable: local-path always errors */
	}

	pgdb_validate_filesystem_path(filepath, "filepath");
	if (base_filepath != NULL)
		pgdb_validate_filesystem_path(base_filepath, "base_filepath");

	db_oid = get_database_oid(dbname, false);
	mode = pg_dbbackup_resolve_mode(db_oid);

	/* Validate type against mode */
	if (mode == BACKUP_MODE_SIMPLE && backup_type == BACKUP_TYPE_LOG)
		ereport(ERROR,
				(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
				 errmsg("log backups require FULL recovery mode"),
				 errhint("Use pg_dbbackup_set_mode('%s', 'full') first.", dbname)));

	ereport(NOTICE,
			(errmsg("pg_dbbackup: backup of \"%s\" to \"%s\" (mode=%s, type=%s, compress=%s, encrypted=%s)",
					dbname, filepath,
					mode == BACKUP_MODE_FULL ? "full" : "simple",
					backup_type == BACKUP_TYPE_FULL ? "full" :
					backup_type == BACKUP_TYPE_DIFFERENTIAL ? "differential" : "log",
					do_compress ? "yes" : "no",
					password ? "yes" : "no")));

	if (mode == BACKUP_MODE_SIMPLE)
	{
		switch (backup_type)
		{
			case BACKUP_TYPE_FULL:
				backup_simple_full(db_oid, dbname, filepath,
								   do_compress, password);
				break;
			case BACKUP_TYPE_DIFFERENTIAL:
				if (base_filepath == NULL || base_filepath[0] == '\0')
					ereport(ERROR,
							(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
							 errmsg("SIMPLE differential backup requires base_filepath"),
							 errhint("Pass the path to the base FULL .bak file via base_filepath := ...")));
				backup_simple_differential(db_oid, dbname, filepath,
											base_filepath, do_compress, password);
				break;
			default:
				ereport(ERROR,
						(errcode(ERRCODE_INTERNAL_ERROR),
						 errmsg("unexpected backup type for SIMPLE mode")));
		}
	}
	else
	{
		switch (backup_type)
		{
			case BACKUP_TYPE_FULL:
				backup_full_full(db_oid, dbname, filepath,
								 do_compress, password);
				break;
			case BACKUP_TYPE_DIFFERENTIAL:
				backup_full_differential(db_oid, dbname, filepath,
										  base_filepath, do_compress, password);
				break;
			case BACKUP_TYPE_LOG:
				backup_full_log(db_oid, dbname, filepath,
								base_filepath, do_compress, password);
				break;
		}
	}

	PG_RETURN_TEXT_P(cstring_to_text(filepath));
}

Datum
pg_dbrestore(PG_FUNCTION_ARGS)
{
	ArrayType  *files_arr = PG_GETARG_ARRAYTYPE_P(0);
	char	   *target_db = PG_ARGISNULL(1) ? NULL : text_to_cstring(PG_GETARG_TEXT_PP(1));
	TimestampTz stop_at = PG_ARGISNULL(2) ? 0 : PG_GETARG_TIMESTAMPTZ(2);
	bool		has_stop_at = !PG_ARGISNULL(2);
	char	   *password = PG_ARGISNULL(3) ? NULL : text_to_cstring(PG_GETARG_TEXT_PP(3));
	Datum	   *file_datums;
	bool	   *file_nulls;
	int			file_count;
	char	  **files;
	int			i;

	if (!superuser())
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
				 errmsg("must be superuser to perform restores")));

	deconstruct_array(files_arr, TEXTOID, -1, false, TYPALIGN_INT,
					  &file_datums, &file_nulls, &file_count);
	for (i = 0; i < file_count; i++)
	{
		if (file_nulls[i])
			continue;
		pgdb_validate_filesystem_path(TextDatumGetCString(file_datums[i]),
									   "restore file");
	}

	if (file_count == 0)
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("at least one .bak file required")));

	files = palloc(sizeof(char *) * file_count);
	for (i = 0; i < file_count; i++)
	{
		if (file_nulls[i])
			ereport(ERROR,
					(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
					 errmsg("file path cannot be NULL")));
		files[i] = TextDatumGetCString(file_datums[i]);
	}

	ereport(NOTICE,
			(errmsg("pg_dbrestore: target=%s, files=%d, pitr=%s",
					target_db ? target_db : "(from header)",
					file_count,
					has_stop_at ? "yes" : "no")));

	{
		BakFileReader *probe = bakfile_open(files[0], password);
		PgDbBackupMode probe_mode = probe->header.mode;
		char	   *fallback_db = pstrdup(probe->header.db_name);

		bakfile_close_reader(probe);

		if (probe_mode == BACKUP_MODE_FULL || has_stop_at)
			restore_full(target_db ? target_db : fallback_db, files, file_count,
						 stop_at, has_stop_at, password);
		else
			restore_simple(target_db ? target_db : fallback_db, files, file_count,
						   password);
	}

	PG_RETURN_VOID();
}

Datum
pg_dbbackup_header(PG_FUNCTION_ARGS)
{
	return inspect_header(fcinfo);
}

Datum
pg_dbbackup_filelist(PG_FUNCTION_ARGS)
{
	return inspect_filelist(fcinfo);
}

Datum
pg_dbbackup_verify(PG_FUNCTION_ARGS)
{
	return inspect_verify(fcinfo);
}

Datum
pg_dbbackup_test_crypto(PG_FUNCTION_ARGS)
{
	bytea	   *input_bytea = PG_GETARG_BYTEA_PP(0);
	bool		do_compress = PG_GETARG_BOOL(1);
	char	   *password = PG_ARGISNULL(2) ? NULL : text_to_cstring(PG_GETARG_TEXT_PP(2));
	const char *input_data = VARDATA_ANY(input_bytea);
	size_t		input_len = VARSIZE_ANY_EXHDR(input_bytea);
	uint8		salt[BAKCRYPTO_SALT_LEN];
	uint8		iv[BAKCRYPTO_IV_LEN];
	BakCryptoContext *wctx;
	BakCryptoContext *rctx;
	void	   *processed = NULL;
	size_t		processed_len;
	void	   *unprocessed = NULL;
	size_t		unprocessed_len;
	bytea	   *result;

	memset(salt, 0, sizeof(salt));
	memset(iv, 0, sizeof(iv));

	wctx = bakcrypto_writer_init(do_compress, password, salt, iv);
	processed_len = bakcrypto_process(wctx, input_data, input_len, &processed);

	rctx = bakcrypto_reader_init(do_compress, password != NULL, password,
								 password != NULL ? salt : NULL,
								 password != NULL ? iv : NULL);
	unprocessed_len = bakcrypto_unprocess(rctx, processed, processed_len,
										  &unprocessed);

	if (unprocessed_len != input_len ||
		(input_len > 0 && memcmp(unprocessed, input_data, input_len) != 0))
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("crypto roundtrip mismatch: got %zu bytes, expected %zu",
						unprocessed_len, input_len)));

	result = (bytea *) palloc(VARHDRSZ + processed_len);
	SET_VARSIZE(result, VARHDRSZ + processed_len);
	if (processed_len > 0)
		memcpy(VARDATA(result), processed, processed_len);

	pfree(processed);
	pfree(unprocessed);
	bakcrypto_free(wctx);
	bakcrypto_free(rctx);

	PG_RETURN_BYTEA_P(result);
}

static Oid
resolve_current_db_oid(const char *dbname)
{
	Oid			db_oid;
	const char *current;

	current = get_database_name(MyDatabaseId);
	if (current == NULL || strcmp(current, dbname) != 0)
		ereport(ERROR,
				(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
				 errmsg("pg_dbbackup_test_* functions only operate on the current database"),
				 errdetail("requested \"%s\", connected to \"%s\"",
						   dbname, current ? current : "(unknown)")));

	db_oid = get_database_oid(dbname, false);
	return db_oid;
}

Datum
pg_dbbackup_test_metadata(PG_FUNCTION_ARGS)
{
	text	   *dbname_text = PG_GETARG_TEXT_PP(0);
	char	   *dbname = text_to_cstring(dbname_text);
	Oid			db_oid = resolve_current_db_oid(dbname);
	StringInfo	sql = metadata_gen_all(db_oid);

	PG_RETURN_TEXT_P(cstring_to_text_with_len(sql->data, sql->len));
}

Datum
pg_dbbackup_test_ddl(PG_FUNCTION_ARGS)
{
	text	   *dbname_text = PG_GETARG_TEXT_PP(0);
	char	   *dbname = text_to_cstring(dbname_text);
	Oid			db_oid = resolve_current_db_oid(dbname);
	StringInfo	sql = ddl_gen_all(db_oid);

	PG_RETURN_TEXT_P(cstring_to_text_with_len(sql->data, sql->len));
}
