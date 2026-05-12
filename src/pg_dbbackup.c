#include "postgres.h"

#include "catalog/namespace.h"
#include "catalog/pg_database.h"
#include "commands/dbcommands.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "postmaster/bgworker.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/guc.h"
#include "utils/lsyscache.h"
#include "utils/syscache.h"

#include "pg_dbbackup.h"
#include "bakfile.h"
#include "bakfile_crypto.h"
#include "backup_simple.h"
#include "backup_full.h"
#include "restore_simple.h"
#include "restore_full.h"
#include "inspect.h"
#include "logical_journal.h"
#include "metadata_gen.h"
#include "ddl_gen.h"

PG_MODULE_MAGIC;

void _PG_init(void);
void pgbu_scheduler_worker_main(Datum main_arg);

bool		pgdb_scheduler_enabled = true;
char	   *pgdb_scheduler_database = NULL;
int			pgdb_scheduler_interval_ms = 60000;

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
		char	   *mode_str;
		bool		isnull;

		mode_str = SPI_getvalue(SPI_tuptable->vals[0],
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

static PgDbBackupType
parse_backup_type(const char *type_str)
{
	if (strcmp(type_str, "full") == 0)
		return BACKUP_TYPE_FULL;
	if (strcmp(type_str, "differential") == 0)
		return BACKUP_TYPE_DIFFERENTIAL;
	if (strcmp(type_str, "log") == 0)
		return BACKUP_TYPE_LOG;

	ereport(ERROR,
			(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
			 errmsg("type must be 'full', 'differential', or 'log'")));
	return BACKUP_TYPE_FULL;		/* unreachable */
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
	PgDbBackupType backup_type = parse_backup_type(text_to_cstring(type_text));
	Oid			db_oid;
	PgDbBackupMode mode;

	if (!superuser())
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
				 errmsg("must be superuser to perform backups")));

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
	text	   *dbname_text = PG_GETARG_TEXT_PP(0);
	ArrayType  *files_arr = PG_GETARG_ARRAYTYPE_P(1);
	char	   *target_db = PG_ARGISNULL(2) ? NULL : text_to_cstring(PG_GETARG_TEXT_PP(2));
	TimestampTz stop_at = PG_ARGISNULL(3) ? 0 : PG_GETARG_TIMESTAMPTZ(3);
	bool		has_stop_at = !PG_ARGISNULL(3);
	char	   *password = PG_ARGISNULL(4) ? NULL : text_to_cstring(PG_GETARG_TEXT_PP(4));
	char	   *dbname = text_to_cstring(dbname_text);
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
			(errmsg("pg_dbrestore: restore of \"%s\" (target=%s, files=%d, pitr=%s)",
					dbname,
					target_db ? target_db : dbname,
					file_count,
					has_stop_at ? "yes" : "no")));

	{
		BakFileReader *probe = bakfile_open(files[0], password);
		PgDbBackupMode probe_mode = probe->header.mode;

		bakfile_close_reader(probe);

		if (probe_mode == BACKUP_MODE_FULL || has_stop_at)
			restore_full(target_db ? target_db : dbname, files, file_count,
						 stop_at, has_stop_at, password);
		else
			restore_simple(target_db ? target_db : dbname, files, file_count,
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
