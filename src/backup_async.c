#include "postgres.h"

#include <unistd.h>

#include "access/xact.h"
#include "catalog/pg_database.h"
#include "commands/dbcommands.h"
#include "executor/spi.h"
#include "fmgr.h"
#include "miscadmin.h"
#include "pgstat.h"
#include "postmaster/bgworker.h"
#include "postmaster/interrupt.h"
#include "storage/ipc.h"
#include "storage/latch.h"
#include "storage/proc.h"
#include "storage/procsignal.h"
#include "tcop/tcopprot.h"
#include "utils/builtins.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"
#include "utils/snapmgr.h"
#include "utils/timestamp.h"
#include "utils/uuid.h"

#include "backup_async.h"
#include "backup_full.h"
#include "backup_simple.h"
#include "pg_dbbackup.h"

PG_FUNCTION_INFO_V1(pg_dbbackup_async);
PG_FUNCTION_INFO_V1(pg_dbbackup_to_storage_async);
PG_FUNCTION_INFO_V1(pg_dbbackup_wait);

#define ASYNC_BGW_LIB "pg_dbbackup"
#define ASYNC_BGW_FN  "pgbu_async_worker_main"

typedef struct AsyncWorkerPayload
{
	pg_uuid_t	backup_id;
	Oid			db_oid;
	int32		requester_pid;
	char		dbname[NAMEDATALEN];
} AsyncWorkerPayload;

StaticAssertDecl(sizeof(AsyncWorkerPayload) <= BGW_EXTRALEN,
				 "AsyncWorkerPayload exceeds bgw_extra capacity");

static PgDbBackupType
parse_backup_type_arg(const char *type_str)
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
	return BACKUP_TYPE_FULL;
}

static pg_uuid_t
generate_backup_uuid(void)
{
	pg_uuid_t	id;
	int			i;

	if (!pg_strong_random(id.data, UUID_LEN))
	{
		for (i = 0; i < UUID_LEN; i++)
			id.data[i] = (uint8) (random() & 0xff);
	}

	id.data[6] = (id.data[6] & 0x0f) | 0x40;
	id.data[8] = (id.data[8] & 0x3f) | 0x80;
	return id;
}

static void
insert_pending_job(const pg_uuid_t *id, const char *dbname,
				   const char *filepath, const char *type,
				   bool compress, bool has_password,
				   const char *base_filepath)
{
	Oid			argtypes[8] = {UUIDOID, TEXTOID, TEXTOID, TEXTOID,
							   BOOLOID, BOOLOID, INT4OID, TEXTOID};
	Datum		values[8];
	char		nulls[8] = {' ', ' ', ' ', ' ', ' ', ' ', ' ', ' '};
	int			ret;

	values[0] = UUIDPGetDatum(id);
	values[1] = CStringGetTextDatum(dbname);
	values[2] = CStringGetTextDatum(filepath);
	values[3] = CStringGetTextDatum(type);
	values[4] = BoolGetDatum(compress);
	values[5] = BoolGetDatum(has_password);
	values[6] = Int32GetDatum((int32) MyProcPid);
	if (base_filepath)
		values[7] = CStringGetTextDatum(base_filepath);
	else
	{
		values[7] = (Datum) 0;
		nulls[7] = 'n';
	}

	SPI_connect();
	ret = SPI_execute_with_args(
		"INSERT INTO dbbackup.backup_jobs "
		"  (backup_id, dbname, destination, filepath, type, compress, has_password, "
		"   requester_pid, base_filepath, status, progress) "
		"VALUES ($1, $2, 'file', $3, $4, $5, $6, $7, $8, 'pending', 0)",
		8, argtypes, values, nulls, false, 0);
	if (ret != SPI_OK_INSERT)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("failed to insert backup job row (SPI %d)", ret)));
	SPI_finish();
}

static void
insert_pending_storage_job(const pg_uuid_t *id, const char *dbname,
						   const char *type, const char *storage_target,
						   const char *backup_set, bool compress,
						   const pg_uuid_t *base_backup_id)
{
	Oid			argtypes[9] = {UUIDOID, TEXTOID, TEXTOID, TEXTOID,
							   TEXTOID, BOOLOID, INT4OID, UUIDOID, BOOLOID};
	Datum		values[9];
	char		nulls[9] = {' ', ' ', ' ', ' ', ' ', ' ', ' ', ' ', ' '};
	int			ret;

	values[0] = UUIDPGetDatum(id);
	values[1] = CStringGetTextDatum(dbname);
	values[2] = CStringGetTextDatum(type);
	if (storage_target)
		values[3] = CStringGetTextDatum(storage_target);
	else
	{
		values[3] = (Datum) 0;
		nulls[3] = 'n';
	}
	if (backup_set)
		values[4] = CStringGetTextDatum(backup_set);
	else
	{
		values[4] = (Datum) 0;
		nulls[4] = 'n';
	}
	values[5] = BoolGetDatum(compress);
	values[6] = Int32GetDatum((int32) MyProcPid);
	if (base_backup_id)
		values[7] = UUIDPGetDatum(base_backup_id);
	else
	{
		values[7] = (Datum) 0;
		nulls[7] = 'n';
	}
	values[8] = BoolGetDatum(false);

	SPI_connect();
	ret = SPI_execute_with_args(
		"INSERT INTO dbbackup.backup_jobs "
		"  (backup_id, dbname, destination, type, storage_target, backup_set, "
		"   compress, requester_pid, base_backup_id, has_password, status, progress) "
		"VALUES ($1, $2, 'storage', $3, $4, $5, $6, $7, $8, $9, 'pending', 0)",
		9, argtypes, values, nulls, false, 0);
	if (ret != SPI_OK_INSERT)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("failed to insert storage backup job row (SPI %d)", ret)));
	SPI_finish();
}

static void
mark_job_status_simple(const pg_uuid_t *id, const char *status)
{
	Oid			argtypes[2] = {TEXTOID, UUIDOID};
	Datum		values[2];

	values[0] = CStringGetTextDatum(status);
	values[1] = UUIDPGetDatum(id);

	SPI_connect();
	SPI_execute_with_args(
		"UPDATE dbbackup.backup_jobs SET status = $1 WHERE backup_id = $2",
		2, argtypes, values, NULL, false, 0);
	SPI_finish();
}

Datum
pg_dbbackup_async(PG_FUNCTION_ARGS)
{
	text	   *dbname_text = PG_GETARG_TEXT_PP(0);
	text	   *filepath_text = PG_GETARG_TEXT_PP(1);
	text	   *type_text = PG_GETARG_TEXT_PP(2);
	bool		do_compress = PG_GETARG_BOOL(3);
	bool		has_password = !PG_ARGISNULL(4);
	char	   *base_filepath = PG_ARGISNULL(5) ? NULL : text_to_cstring(PG_GETARG_TEXT_PP(5));
	char	   *dbname = text_to_cstring(dbname_text);
	char	   *filepath = text_to_cstring(filepath_text);
	char	   *type_str = text_to_cstring(type_text);
	Oid			db_oid;
	pg_uuid_t	backup_id;
	BackgroundWorker worker;
	BackgroundWorkerHandle *handle;
	AsyncWorkerPayload payload;
	pg_uuid_t  *result_id;

	if (!superuser())
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
				 errmsg("must be superuser to perform backups")));

	(void) parse_backup_type_arg(type_str);

	db_oid = get_database_oid(dbname, false);

	backup_id = generate_backup_uuid();

	insert_pending_job(&backup_id, dbname, filepath, type_str,
					   do_compress, has_password, base_filepath);

	memset(&worker, 0, sizeof(worker));
	worker.bgw_flags = BGWORKER_SHMEM_ACCESS | BGWORKER_BACKEND_DATABASE_CONNECTION;
	worker.bgw_start_time = BgWorkerStart_RecoveryFinished;
	worker.bgw_restart_time = BGW_NEVER_RESTART;
	strlcpy(worker.bgw_library_name, ASYNC_BGW_LIB, BGW_MAXLEN);
	strlcpy(worker.bgw_function_name, ASYNC_BGW_FN, BGW_MAXLEN);
	snprintf(worker.bgw_name, BGW_MAXLEN, "pg_dbbackup async backup");
	snprintf(worker.bgw_type, BGW_MAXLEN, "pg_dbbackup");
	worker.bgw_notify_pid = MyProcPid;
	worker.bgw_main_arg = (Datum) 0;

	memset(&payload, 0, sizeof(payload));
	payload.backup_id = backup_id;
	payload.db_oid = db_oid;
	payload.requester_pid = (int32) MyProcPid;
	strlcpy(payload.dbname, dbname, sizeof(payload.dbname));
	memcpy(worker.bgw_extra, &payload, sizeof(payload));

	if (!RegisterDynamicBackgroundWorker(&worker, &handle))
	{
		mark_job_status_simple(&backup_id, "failed");
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_RESOURCES),
				 errmsg("could not register pg_dbbackup async worker"),
				 errhint("Increase max_worker_processes or wait for existing workers to finish.")));
	}

	pfree(handle);

	result_id = (pg_uuid_t *) palloc(sizeof(pg_uuid_t));
	*result_id = backup_id;
	PG_RETURN_POINTER(result_id);
}

Datum
pg_dbbackup_to_storage_async(PG_FUNCTION_ARGS)
{
	text	   *dbname_text = PG_GETARG_TEXT_PP(0);
	text	   *type_text = PG_GETARG_TEXT_PP(1);
	char	   *storage_target = PG_ARGISNULL(2) ? NULL :
		text_to_cstring(PG_GETARG_TEXT_PP(2));
	char	   *backup_set = PG_ARGISNULL(3) ? NULL :
		text_to_cstring(PG_GETARG_TEXT_PP(3));
	bool		do_compress = PG_GETARG_BOOL(4);
	pg_uuid_t  *base_backup_id = PG_ARGISNULL(5) ? NULL : PG_GETARG_UUID_P(5);
	char	   *dbname = text_to_cstring(dbname_text);
	char	   *type_str = text_to_cstring(type_text);
	Oid			db_oid;
	pg_uuid_t	backup_id;
	BackgroundWorker worker;
	BackgroundWorkerHandle *handle;
	AsyncWorkerPayload payload;
	pg_uuid_t  *result_id;

	if (!superuser())
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
				 errmsg("must be superuser to perform backups")));

	(void) parse_backup_type_arg(type_str);

	db_oid = get_database_oid(dbname, false);
	backup_id = generate_backup_uuid();

	insert_pending_storage_job(&backup_id, dbname, type_str, storage_target,
							   backup_set, do_compress, base_backup_id);

	memset(&worker, 0, sizeof(worker));
	worker.bgw_flags = BGWORKER_SHMEM_ACCESS | BGWORKER_BACKEND_DATABASE_CONNECTION;
	worker.bgw_start_time = BgWorkerStart_RecoveryFinished;
	worker.bgw_restart_time = BGW_NEVER_RESTART;
	strlcpy(worker.bgw_library_name, ASYNC_BGW_LIB, BGW_MAXLEN);
	strlcpy(worker.bgw_function_name, ASYNC_BGW_FN, BGW_MAXLEN);
	snprintf(worker.bgw_name, BGW_MAXLEN, "pg_dbbackup async S3 backup");
	snprintf(worker.bgw_type, BGW_MAXLEN, "pg_dbbackup");
	worker.bgw_notify_pid = MyProcPid;
	worker.bgw_main_arg = (Datum) 0;

	memset(&payload, 0, sizeof(payload));
	payload.backup_id = backup_id;
	payload.db_oid = db_oid;
	payload.requester_pid = (int32) MyProcPid;
	strlcpy(payload.dbname, dbname, sizeof(payload.dbname));
	memcpy(worker.bgw_extra, &payload, sizeof(payload));

	if (!RegisterDynamicBackgroundWorker(&worker, &handle))
	{
		mark_job_status_simple(&backup_id, "failed");
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_RESOURCES),
				 errmsg("could not register pg_dbbackup storage async worker"),
				 errhint("Increase max_worker_processes or wait for existing workers to finish.")));
	}

	pfree(handle);

	result_id = (pg_uuid_t *) palloc(sizeof(pg_uuid_t));
	*result_id = backup_id;
	PG_RETURN_POINTER(result_id);
}

Datum
pg_dbbackup_wait(PG_FUNCTION_ARGS)
{
	pg_uuid_t  *id = PG_GETARG_UUID_P(0);
	int32		timeout_secs = PG_GETARG_INT32(1);
	TimestampTz deadline;
	char	   *final_status = NULL;

	if (timeout_secs <= 0)
		timeout_secs = 300;

	deadline = TimestampTzPlusMilliseconds(GetCurrentTimestamp(),
										   (int64) timeout_secs * 1000);

	{
		MemoryContext caller_cxt = CurrentMemoryContext;

		for (;;)
		{
			Oid			argtypes[1] = {UUIDOID};
			Datum		values[1];
			char	   *status;
			char	   *status_copy = NULL;
			int			rc;
			Snapshot	snap;
			bool		terminal = false;

			CHECK_FOR_INTERRUPTS();

			values[0] = UUIDPGetDatum(id);

			snap = GetLatestSnapshot();
			PushActiveSnapshot(snap);

			SPI_connect();
			SPI_execute_with_args(
				"SELECT status FROM dbbackup.backup_jobs WHERE backup_id = $1",
				1, argtypes, values, NULL, false, 1);
			if (SPI_processed == 0)
			{
				SPI_finish();
				PopActiveSnapshot();
				ereport(ERROR,
						(errcode(ERRCODE_NO_DATA_FOUND),
						 errmsg("backup job not found")));
			}
			status = SPI_getvalue(SPI_tuptable->vals[0],
								  SPI_tuptable->tupdesc, 1);
			if (status)
			{
				MemoryContext old = MemoryContextSwitchTo(caller_cxt);
				status_copy = pstrdup(status);
				MemoryContextSwitchTo(old);
				if (strcmp(status, "completed") == 0 ||
					strcmp(status, "failed") == 0)
					terminal = true;
			}
			SPI_finish();
			PopActiveSnapshot();

			if (terminal)
			{
				final_status = status_copy;
				break;
			}

			if (GetCurrentTimestamp() >= deadline)
			{
				final_status = status_copy ? status_copy
											: MemoryContextStrdup(caller_cxt, "pending");
				break;
			}

			rc = WaitLatch(MyLatch,
						   WL_LATCH_SET | WL_TIMEOUT | WL_EXIT_ON_PM_DEATH,
						   200L, PG_WAIT_EXTENSION);
			ResetLatch(MyLatch);
			if (rc & WL_POSTMASTER_DEATH)
				proc_exit(1);
		}
	}

	PG_RETURN_TEXT_P(cstring_to_text(final_status));
}

static void
worker_dispatch_backup(const AsyncWorkerPayload *payload,
					   const char *filepath, const char *type_str,
					   bool compress, const char *password,
					   const char *base_filepath)
{
	PgDbBackupType backup_type = parse_backup_type_arg(type_str);
	PgDbBackupMode mode = pg_dbbackup_resolve_mode(payload->db_oid);

	if (mode == BACKUP_MODE_SIMPLE && backup_type == BACKUP_TYPE_LOG)
		ereport(ERROR,
				(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
				 errmsg("log backups require FULL recovery mode")));

	if (mode == BACKUP_MODE_SIMPLE)
	{
		switch (backup_type)
		{
			case BACKUP_TYPE_FULL:
				backup_simple_full(payload->db_oid, payload->dbname, filepath,
								   compress, password);
				break;
			case BACKUP_TYPE_DIFFERENTIAL:
				if (base_filepath == NULL || base_filepath[0] == '\0')
					ereport(ERROR,
							(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
							 errmsg("SIMPLE differential backup requires base_filepath")));
				backup_simple_differential(payload->db_oid, payload->dbname,
										   filepath, base_filepath,
										   compress, password);
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
				backup_full_full(payload->db_oid, payload->dbname, filepath,
								 compress, password);
				break;
			case BACKUP_TYPE_DIFFERENTIAL:
				backup_full_differential(payload->db_oid, payload->dbname,
										 filepath, base_filepath,
										 compress, password);
				break;
			case BACKUP_TYPE_LOG:
				backup_full_log(payload->db_oid, payload->dbname, filepath,
								base_filepath, compress, password);
				break;
		}
	}
}

static void
worker_load_job(const pg_uuid_t *id,
				char **destination_out, char **filepath_out, char **type_out,
				bool *compress_out, char **base_filepath_out,
				char **storage_target_out, char **backup_set_out,
				char **base_backup_id_out)
{
	Oid			argtypes[1] = {UUIDOID};
	Datum		values[1];
	bool		isnull;
	MemoryContext oldcxt;
	char	   *fp;
	char	   *tp;
	char	   *dest;
	bool		comp;
	char	   *bp = NULL;
	char	   *st = NULL;
	char	   *bs = NULL;
	char	   *bb = NULL;
	Datum		d;

	values[0] = UUIDPGetDatum(id);

	SPI_connect();
	SPI_execute_with_args(
		"SELECT destination, filepath, type, compress, base_filepath, "
		"       storage_target, backup_set, base_backup_id::text "
		"  FROM dbbackup.backup_jobs WHERE backup_id = $1",
		1, argtypes, values, NULL, true, 1);
	if (SPI_processed == 0)
	{
		SPI_finish();
		ereport(ERROR,
				(errcode(ERRCODE_NO_DATA_FOUND),
				 errmsg("backup job row missing in worker")));
	}

	oldcxt = MemoryContextSwitchTo(TopMemoryContext);

	d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
	dest = pstrdup(TextDatumGetCString(d));

	d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 2, &isnull);
	fp = isnull ? NULL : pstrdup(TextDatumGetCString(d));

	d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 3, &isnull);
	tp = pstrdup(TextDatumGetCString(d));

	d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 4, &isnull);
	comp = DatumGetBool(d);

	d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 5, &isnull);
	if (!isnull)
		bp = pstrdup(TextDatumGetCString(d));

	d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 6, &isnull);
	if (!isnull)
		st = pstrdup(TextDatumGetCString(d));

	d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 7, &isnull);
	if (!isnull)
		bs = pstrdup(TextDatumGetCString(d));

	d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 8, &isnull);
	if (!isnull)
		bb = pstrdup(TextDatumGetCString(d));

	MemoryContextSwitchTo(oldcxt);

	SPI_finish();

	*destination_out = dest;
	*filepath_out = fp;
	*type_out = tp;
	*compress_out = comp;
	*base_filepath_out = bp;
	*storage_target_out = st;
	*backup_set_out = bs;
	*base_backup_id_out = bb;
}

static void
worker_mark_running(const pg_uuid_t *id)
{
	Oid			argtypes[1] = {UUIDOID};
	Datum		values[1];

	values[0] = UUIDPGetDatum(id);

	SPI_connect();
	SPI_execute_with_args(
		"UPDATE dbbackup.backup_jobs "
		"   SET status = 'running', started_at = now(), progress = 5 "
		" WHERE backup_id = $1",
		1, argtypes, values, NULL, false, 0);
	SPI_finish();
}

static void
worker_mark_completed(const pg_uuid_t *id)
{
	Oid			argtypes[1] = {UUIDOID};
	Datum		values[1];

	values[0] = UUIDPGetDatum(id);

	SPI_connect();
	SPI_execute_with_args(
		"UPDATE dbbackup.backup_jobs "
		"   SET status = 'completed', completed_at = now(), progress = 100 "
		" WHERE backup_id = $1",
		1, argtypes, values, NULL, false, 0);
	SPI_finish();
}

static void
worker_mark_failed(const pg_uuid_t *id, const char *err)
{
	Oid			argtypes[2] = {TEXTOID, UUIDOID};
	Datum		values[2];
	char		nulls[2] = {' ', ' '};

	if (err)
		values[0] = CStringGetTextDatum(err);
	else
	{
		values[0] = (Datum) 0;
		nulls[0] = 'n';
	}
	values[1] = UUIDPGetDatum(id);

	SPI_connect();
	SPI_execute_with_args(
		"UPDATE dbbackup.backup_jobs "
		"   SET status = 'failed', completed_at = now(), error_msg = $1 "
		" WHERE backup_id = $2",
		2, argtypes, values, nulls, false, 0);
	SPI_finish();
}

static void
worker_dispatch_storage_backup(const AsyncWorkerPayload *payload,
							   const char *type_str, bool compress,
							   const char *storage_target,
							   const char *backup_set,
							   const char *base_backup_id)
{
	Oid			argtypes[6] = {TEXTOID, TEXTOID, TEXTOID, TEXTOID,
							   BOOLOID, TEXTOID};
	Datum		values[6];
	char		nulls[6] = {' ', ' ', ' ', ' ', ' ', ' '};
	int			ret;

	values[0] = CStringGetTextDatum(payload->dbname);
	values[1] = CStringGetTextDatum(type_str);
	if (storage_target)
		values[2] = CStringGetTextDatum(storage_target);
	else
	{
		values[2] = (Datum) 0;
		nulls[2] = 'n';
	}
	if (backup_set)
		values[3] = CStringGetTextDatum(backup_set);
	else
	{
		values[3] = (Datum) 0;
		nulls[3] = 'n';
	}
	values[4] = BoolGetDatum(compress);
	if (base_backup_id)
		values[5] = CStringGetTextDatum(base_backup_id);
	else
	{
		values[5] = (Datum) 0;
		nulls[5] = 'n';
	}

	SPI_connect();
	ret = SPI_execute_with_args(
		"SELECT dbbackup.pg_dbbackup_to_storage("
		"  dbname := $1, type := $2, storage_target := $3, backup_set := $4, "
		"  compress := $5, base_backup_id := $6::uuid)",
		6, argtypes, values, nulls, false, 1);
	SPI_finish();

	if (ret != SPI_OK_SELECT)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("storage backup worker failed to dispatch backup (SPI %d)",
						ret)));
}

void
pgbu_async_worker_main(Datum main_arg)
{
	AsyncWorkerPayload payload;
	char	   *destination = NULL;
	char	   *filepath = NULL;
	char	   *type_str = NULL;
	bool		compress = false;
	char	   *base_filepath = NULL;
	char	   *storage_target = NULL;
	char	   *backup_set = NULL;
	char	   *base_backup_id = NULL;
	bool		failed = false;
	char	   *fail_msg = NULL;
	MemoryContext worker_cxt;

	(void) main_arg;

	pqsignal(SIGTERM, die);
	pqsignal(SIGHUP, SignalHandlerForConfigReload);
	BackgroundWorkerUnblockSignals();

	if (MyBgworkerEntry == NULL)
		proc_exit(1);
	memcpy(&payload, MyBgworkerEntry->bgw_extra, sizeof(payload));

	BackgroundWorkerInitializeConnection(payload.dbname, NULL, 0);

	worker_cxt = AllocSetContextCreate(TopMemoryContext,
									   "pg_dbbackup async worker",
									   ALLOCSET_DEFAULT_SIZES);
	MemoryContextSwitchTo(worker_cxt);

	pgstat_report_activity(STATE_RUNNING, "pg_dbbackup async backup");

	{
		int			tries;

		for (tries = 0; tries < 200; tries++)
		{
			bool		found = false;
			Oid			argtypes[1] = {UUIDOID};
			Datum		values[1];

			values[0] = UUIDPGetDatum(&payload.backup_id);

			StartTransactionCommand();
			PushActiveSnapshot(GetTransactionSnapshot());
			SPI_connect();
			SPI_execute_with_args(
				"SELECT 1 FROM dbbackup.backup_jobs WHERE backup_id = $1",
				1, argtypes, values, NULL, true, 1);
			found = (SPI_processed > 0);
			SPI_finish();
			PopActiveSnapshot();
			CommitTransactionCommand();

			if (found)
				break;

			(void) WaitLatch(MyLatch,
							 WL_LATCH_SET | WL_TIMEOUT | WL_EXIT_ON_PM_DEATH,
							 50L, PG_WAIT_EXTENSION);
			ResetLatch(MyLatch);
		}
	}

	StartTransactionCommand();
	PushActiveSnapshot(GetTransactionSnapshot());
	worker_load_job(&payload.backup_id,
					&destination, &filepath, &type_str, &compress,
					&base_filepath, &storage_target, &backup_set,
					&base_backup_id);
	worker_mark_running(&payload.backup_id);
	PopActiveSnapshot();
	CommitTransactionCommand();

	PG_TRY();
	{
		StartTransactionCommand();
		PushActiveSnapshot(GetTransactionSnapshot());
		if (destination && strcmp(destination, "storage") == 0)
			worker_dispatch_storage_backup(&payload, type_str, compress,
										   storage_target, backup_set,
										   base_backup_id);
		else
			worker_dispatch_backup(&payload, filepath, type_str, compress,
								   NULL, base_filepath);
		PopActiveSnapshot();
		CommitTransactionCommand();
	}
	PG_CATCH();
	{
		ErrorData  *edata;
		MemoryContext ecxt;

		ecxt = MemoryContextSwitchTo(worker_cxt);
		edata = CopyErrorData();
		fail_msg = pstrdup(edata->message ? edata->message : "(unknown error)");
		FreeErrorData(edata);
		FlushErrorState();
		MemoryContextSwitchTo(ecxt);

		AbortCurrentTransaction();
		failed = true;
	}
	PG_END_TRY();

	StartTransactionCommand();
	PushActiveSnapshot(GetTransactionSnapshot());
	if (failed)
		worker_mark_failed(&payload.backup_id, fail_msg);
	else
		worker_mark_completed(&payload.backup_id);
	PopActiveSnapshot();
	CommitTransactionCommand();

	pgstat_report_activity(STATE_IDLE, NULL);

	proc_exit(failed ? 1 : 0);
}
