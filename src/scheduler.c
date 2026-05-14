#include "postgres.h"

#include <dirent.h>
#include <errno.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

#include "access/xact.h"
#include "executor/spi.h"
#include "fmgr.h"
#include "miscadmin.h"
#include "pgstat.h"
#include "postmaster/bgworker.h"
#include "postmaster/interrupt.h"
#include "storage/ipc.h"
#include "storage/latch.h"
#include "tcop/tcopprot.h"
#include "utils/builtins.h"
#include "utils/memutils.h"
#include "utils/snapmgr.h"

#include "pg_dbbackup.h"

/* Stale-async-job reaper threshold: any backup_jobs row in
 * pending/running with no progress past this age is assumed to belong
 * to a crashed background worker and gets marked failed. One hour gives
 * a generous upper bound on legitimate long backups while still letting
 * orphans drain out of pg_dbbackup_status() within a single tick.
 */
#define PGDB_STALE_JOB_AGE_SECONDS	3600

/* Tmp-file sweeper threshold: pg_dbbackup_*.bak* files in /tmp that
 * are older than this are presumed orphaned by a crashed backend.
 */
#define PGDB_TMP_FILE_AGE_SECONDS	3600
#define PGDB_TMP_DIR				"/tmp"
#define PGDB_TMP_PREFIX				"pg_dbbackup_"

void		pgbu_scheduler_worker_main(Datum main_arg);

static volatile sig_atomic_t scheduler_got_sigterm = false;
static volatile sig_atomic_t scheduler_got_sighup = false;

static void
scheduler_sigterm(SIGNAL_ARGS)
{
	int			save_errno = errno;

	scheduler_got_sigterm = true;
	SetLatch(MyLatch);
	errno = save_errno;
}

static void
scheduler_sighup(SIGNAL_ARGS)
{
	int			save_errno = errno;

	scheduler_got_sighup = true;
	SetLatch(MyLatch);
	errno = save_errno;
}

static bool
scheduler_function_exists(void)
{
	int			ret;
	bool		exists = false;

	ret = SPI_execute(
		"SELECT to_regprocedure('dbbackup.pg_dbbackup_run_due_schedules(timestamptz)') IS NOT NULL",
		true, 1);
	if (ret == SPI_OK_SELECT && SPI_processed == 1)
	{
		char	   *v = SPI_getvalue(SPI_tuptable->vals[0],
									 SPI_tuptable->tupdesc, 1);

		exists = v != NULL && v[0] == 't';
	}
	return exists;
}

static bool
backup_jobs_table_exists(void)
{
	int			ret;
	bool		exists = false;

	ret = SPI_execute(
		"SELECT to_regclass('dbbackup.backup_jobs') IS NOT NULL",
		true, 1);
	if (ret == SPI_OK_SELECT && SPI_processed == 1)
	{
		char	   *v = SPI_getvalue(SPI_tuptable->vals[0],
									 SPI_tuptable->tupdesc, 1);

		exists = v != NULL && v[0] == 't';
	}
	return exists;
}

static void
reap_stale_async_jobs(void)
{
	int			ret;
	StringInfoData sql;

	if (!backup_jobs_table_exists())
		return;

	initStringInfo(&sql);
	appendStringInfo(&sql,
					 "UPDATE dbbackup.backup_jobs "
					 "   SET status = 'failed', "
					 "       completed_at = now(), "
					 "       error_msg = COALESCE(error_msg, "
					 "         'pg_dbbackup: background worker terminated before completion') "
					 " WHERE status IN ('pending', 'running') "
					 "   AND COALESCE(started_at, created_at) "
					 "         < now() - make_interval(secs => %d)",
					 PGDB_STALE_JOB_AGE_SECONDS);

	ret = SPI_execute(sql.data, false, 0);
	pfree(sql.data);

	if (ret == SPI_OK_UPDATE && SPI_processed > 0)
		ereport(LOG,
				(errmsg("pg_dbbackup: reaped %lu stale async backup job(s)",
						(unsigned long) SPI_processed)));
}

static int
sweep_orphan_tmp_files_ex(int min_age_seconds)
{
	DIR		   *dir;
	struct dirent *de;
	time_t		now = time(NULL);
	size_t		prefix_len = strlen(PGDB_TMP_PREFIX);
	int			unlinked = 0;

	dir = opendir(PGDB_TMP_DIR);
	if (dir == NULL)
		return 0;

	while ((de = readdir(dir)) != NULL)
	{
		char		path[1024];
		struct stat st;
		size_t		name_len;

		if (strncmp(de->d_name, PGDB_TMP_PREFIX, prefix_len) != 0)
			continue;

		name_len = strlen(de->d_name);
		/* Match *.bak or *.bak.tmp suffixes only (avoid stomping unrelated
		 * files an operator may have parked under the same prefix). */
		if (!((name_len > 4 &&
			   strcmp(de->d_name + name_len - 4, ".bak") == 0) ||
			  (name_len > 8 &&
			   strcmp(de->d_name + name_len - 8, ".bak.tmp") == 0)))
			continue;

		snprintf(path, sizeof(path), "%s/%s", PGDB_TMP_DIR, de->d_name);
		if (stat(path, &st) != 0)
			continue;
		if (!S_ISREG(st.st_mode))
			continue;
		if ((now - st.st_mtime) < min_age_seconds)
			continue;

		if (unlink(path) == 0)
			unlinked++;
	}

	closedir(dir);

	if (unlinked > 0)
		ereport(LOG,
				(errmsg("pg_dbbackup: swept %d orphan tmp file(s) from %s",
						unlinked, PGDB_TMP_DIR)));
	return unlinked;
}

static void
sweep_orphan_tmp_files(void)
{
	(void) sweep_orphan_tmp_files_ex(PGDB_TMP_FILE_AGE_SECONDS);
}

PG_FUNCTION_INFO_V1(pg_dbbackup_sweep_orphan_tmp_files);
Datum
pg_dbbackup_sweep_orphan_tmp_files(PG_FUNCTION_ARGS)
{
	int32		min_age = PG_GETARG_INT32(0);
	int			n;

	if (min_age < 0)
		min_age = 0;
	n = sweep_orphan_tmp_files_ex(min_age);
	PG_RETURN_INT32(n);
}

static void
scheduler_run_once(void)
{
	bool		failed = false;
	char	   *failure = NULL;
	MemoryContext run_cxt;

	run_cxt = AllocSetContextCreate(CurrentMemoryContext,
									"pg_dbbackup scheduler tick",
									ALLOCSET_DEFAULT_SIZES);

	PG_TRY();
	{
		MemoryContext old = MemoryContextSwitchTo(run_cxt);

		StartTransactionCommand();
		PushActiveSnapshot(GetTransactionSnapshot());
		SPI_connect();

		if (scheduler_function_exists())
			SPI_execute(
				"SELECT dbbackup.pg_dbbackup_run_due_schedules(clock_timestamp())",
				false, 1);

		reap_stale_async_jobs();

		SPI_finish();
		PopActiveSnapshot();
		CommitTransactionCommand();

		sweep_orphan_tmp_files();

		MemoryContextSwitchTo(old);
	}
	PG_CATCH();
	{
		ErrorData  *edata;
		MemoryContext old;

		old = MemoryContextSwitchTo(run_cxt);
		edata = CopyErrorData();
		failure = pstrdup(edata->message ? edata->message : "unknown error");
		FreeErrorData(edata);
		FlushErrorState();
		MemoryContextSwitchTo(old);

		AbortCurrentTransaction();
		failed = true;
	}
	PG_END_TRY();

	if (failed)
		ereport(WARNING,
				(errmsg("pg_dbbackup scheduler tick failed: %s", failure)));

	MemoryContextDelete(run_cxt);
}

void
pgbu_scheduler_worker_main(Datum main_arg)
{
	(void) main_arg;

	pqsignal(SIGTERM, scheduler_sigterm);
	pqsignal(SIGHUP, scheduler_sighup);
	BackgroundWorkerUnblockSignals();

	BackgroundWorkerInitializeConnection(pgdb_scheduler_database, NULL, 0);
	pgstat_report_activity(STATE_IDLE, "pg_dbbackup scheduler");

	while (!scheduler_got_sigterm)
	{
		int			rc;

		CHECK_FOR_INTERRUPTS();
		if (scheduler_got_sighup)
		{
			scheduler_got_sighup = false;
			ProcessConfigFile(PGC_SIGHUP);
		}

		pgstat_report_activity(STATE_RUNNING,
							   "pg_dbbackup scheduler running due schedules");
		scheduler_run_once();
		pgstat_report_activity(STATE_IDLE, "pg_dbbackup scheduler");

		rc = WaitLatch(MyLatch,
					   WL_LATCH_SET | WL_TIMEOUT | WL_EXIT_ON_PM_DEATH,
					   pgdb_scheduler_interval_ms,
					   PG_WAIT_EXTENSION);
		ResetLatch(MyLatch);
		if (rc & WL_POSTMASTER_DEATH)
			proc_exit(1);
	}

	proc_exit(0);
}
