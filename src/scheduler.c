#include "postgres.h"

#include <errno.h>

#include "access/xact.h"
#include "executor/spi.h"
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

		SPI_finish();
		PopActiveSnapshot();
		CommitTransactionCommand();

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
