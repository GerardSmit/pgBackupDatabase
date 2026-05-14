#include "postgres.h"

#include <unistd.h>

#include "access/xlog.h"
#include "catalog/pg_database.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "lib/stringinfo.h"
#include "miscadmin.h"
#include "port/pg_bswap.h"
#include "storage/fd.h"
#include "storage/lmgr.h"
#include "utils/builtins.h"
#include "utils/memutils.h"
#include "utils/pg_lsn.h"
#include "utils/timestamp.h"

#include "backup_full.h"
#include "backup_simple.h"
#include "bakfile.h"
#include "logical_journal.h"
#include "metadata_gen.h"

/*
 * Inside appendStringInfo we need the printf-format-escaped variant; the
 * plain variant is used for direct SPI_execute strings further down.
 */
#define SKIP_SYSTEM_NSP_SQL PGDBBACKUP_SKIP_SYSTEM_NSP_FMT

static XLogRecPtr advance_logical_slot_to_lsn(const char *slot_name,
											   XLogRecPtr upto_lsn);
static void upsert_logical_chain(Oid db_oid, const char *db_name,
								  const char *slot_name,
								  XLogRecPtr confirmed_lsn);
static XLogRecPtr slot_confirmed_flush_lsn(const char *slot_name);

static void
capture_db_xid_bounds(Oid db_oid, uint32 *frozen_xid_out, uint32 *min_mxid_out)
{
	int			ret;
	bool		isnull;
	Datum		d;

	*frozen_xid_out = 0;
	*min_mxid_out = 0;

	SPI_connect();
	ret = SPI_execute_with_args(
		"SELECT datfrozenxid::text::int8, datminmxid::text::int8 "
		"FROM pg_database WHERE oid = $1",
		1,
		(Oid[]){OIDOID},
		(Datum[]){ObjectIdGetDatum(db_oid)},
		NULL, true, 1);

	if (ret == SPI_OK_SELECT && SPI_processed == 1)
	{
		d = SPI_getbinval(SPI_tuptable->vals[0],
						   SPI_tuptable->tupdesc, 1, &isnull);
		if (!isnull)
			*frozen_xid_out = (uint32) DatumGetInt64(d);
		d = SPI_getbinval(SPI_tuptable->vals[0],
						   SPI_tuptable->tupdesc, 2, &isnull);
		if (!isnull)
			*min_mxid_out = (uint32) DatumGetInt64(d);
	}
	SPI_finish();
}

static void
ensure_logical_journal_preloaded(void)
{
	if (!pgdb_logical_journal_loaded_by_shared_preload())
		ereport(ERROR,
				(errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
				 errmsg("FULL logical PITR requires pg_dbbackup in shared_preload_libraries"),
				 errdetail("Sequence and large-object changes are captured by a transaction-end journal that must be loaded in every writer backend."),
				 errhint("Set shared_preload_libraries = 'pg_dbbackup' (plus any other required libraries) and restart PostgreSQL.")));
}

static bool
timescale_continuous_agg_catalog_exists(void)
{
	int			ret;
	bool		exists = false;

	ret = SPI_execute(
		"SELECT to_regclass('_timescaledb_catalog.continuous_agg') IS NOT NULL",
		true, 1);
	if (ret == SPI_OK_SELECT && SPI_processed == 1)
	{
		char	   *v = SPI_getvalue(SPI_tuptable->vals[0],
									 SPI_tuptable->tupdesc, 1);

		exists = (v != NULL && v[0] == 't');
	}
	return exists;
}

static void
validate_full_feature_matrix(Oid db_oid)
{
	StringInfoData sql;
	int			ret;
	bool		has_cagg_catalog;

	SPI_connect();
	has_cagg_catalog = timescale_continuous_agg_catalog_exists();

	initStringInfo(&sql);
	(void) has_cagg_catalog;
	(void) db_oid;
	appendStringInfoString(&sql,
					 "WITH unsupported AS ("
					 "  SELECT n.nspname AS schema_name, c.relname AS object_name, "
					 "         'unlogged tables' AS reason "
					 "  FROM pg_class c "
					 "  JOIN pg_namespace n ON c.relnamespace = n.oid "
					 "  WHERE " SKIP_SYSTEM_NSP_SQL " "
					 "    AND c.relkind IN ('r', 'p') "
					 "    AND c.relpersistence = 'u' "
					 "    AND NOT EXISTS ("
					 "      SELECT 1 FROM pg_depend d "
					 "      WHERE d.objid = c.oid AND d.deptype = 'e'"
					 "    ) "
					 "UNION ALL "
					 "  SELECT n.nspname, t.typname, 'user-defined base/pseudo types' "
					 "  FROM pg_type t "
					 "  JOIN pg_namespace n ON t.typnamespace = n.oid "
					 "  WHERE " SKIP_SYSTEM_NSP_SQL " "
					 "    AND t.typtype IN ('b', 'p') "
					 "    AND t.typelem = 0 "
					 "    AND t.typrelid = 0 "
					 "    AND NOT EXISTS ("
					 "      SELECT 1 FROM pg_depend d "
					 "      WHERE d.objid = t.oid AND d.deptype = 'e'"
					 "    ) "
					 ") "
					 "SELECT schema_name, object_name, reason "
					 "FROM unsupported "
					 "WHERE reason IS NOT NULL "
					 "ORDER BY schema_name, object_name "
					 "LIMIT 1");

	ret = SPI_execute(sql.data, true, 1);
	pfree(sql.data);

	if (ret != SPI_OK_SELECT)
	{
		SPI_finish();
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("failed to validate FULL logical PITR feature matrix (rc=%d)",
						ret)));
	}

	if (SPI_processed > 0)
	{
		char	   *nsp = SPI_getvalue(SPI_tuptable->vals[0],
									   SPI_tuptable->tupdesc, 1);
		char	   *obj = SPI_getvalue(SPI_tuptable->vals[0],
									   SPI_tuptable->tupdesc, 2);
		char	   *reason = SPI_getvalue(SPI_tuptable->vals[0],
										   SPI_tuptable->tupdesc, 3);

		if (nsp == NULL)
			nsp = "?";
		if (obj == NULL)
			obj = "?";
		if (reason == NULL)
			reason = "unsupported database objects";

		SPI_finish();
		ereport(ERROR,
				(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
				 errmsg("FULL logical PITR does not support %s", reason),
				 errdetail("Unsupported object: %s.%s", nsp, obj),
				 errhint("Remove this object, convert it to a supported logical representation, or add a pg_dbbackup adapter before taking a FULL-mode backup.")));
	}

	SPI_finish();
}

static char *
logical_slot_name_for_db(Oid db_oid)
{
	return psprintf("_pg_dbbackup_%u", db_oid);
}

static void
format_lsn(XLogRecPtr lsn, char *buf, size_t buflen)
{
	snprintf(buf, buflen, "%X/%X",
			 (uint32) (lsn >> 32), (uint32) lsn);
}

static bool
parse_lsn_text(const char *s, XLogRecPtr *lsn_out)
{
	uint32		hi,
				lo;

	if (s == NULL || sscanf(s, "%X/%X", &hi, &lo) != 2)
		return false;

	*lsn_out = ((XLogRecPtr) hi << 32) | lo;
	return true;
}

static bool
logical_replication_slot_exists(const char *slot_name)
{
	int			ret;
	bool		exists;
	bool		isnull;

	SPI_connect();
	ret = SPI_execute_with_args(
		"SELECT EXISTS (SELECT 1 FROM pg_replication_slots WHERE slot_name = $1)",
		1,
		(Oid[]){TEXTOID},
		(Datum[]){CStringGetTextDatum(slot_name)},
		NULL, true, 1);

	if (ret != SPI_OK_SELECT || SPI_processed != 1)
	{
		SPI_finish();
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not inspect replication slot \"%s\" (rc=%d)",
						slot_name, ret)));
	}

	exists = DatumGetBool(SPI_getbinval(SPI_tuptable->vals[0],
										SPI_tuptable->tupdesc, 1, &isnull));
	SPI_finish();
	return exists;
}

static void
drop_replication_slot_if_exists(const char *slot_name)
{
	int			ret;

	SPI_connect();
	ret = SPI_execute_with_args(
		"SELECT pg_drop_replication_slot($1) "
		"WHERE EXISTS (SELECT 1 FROM pg_replication_slots WHERE slot_name = $1)",
		1,
		(Oid[]){TEXTOID},
		(Datum[]){CStringGetTextDatum(slot_name)},
		NULL, false, 1);
	SPI_finish();

	if (ret != SPI_OK_SELECT)
		elog(WARNING, "could not drop replication slot \"%s\" (rc=%d)",
			 slot_name, ret);
}

static void
lock_logical_full_tables(Oid db_oid)
{
	int			ret;
	uint64		i;

	SPI_connect();

	ret = SPI_execute(
		"SELECT n.nspname, c.relname, c.relreplident, "
		"EXISTS (SELECT 1 FROM pg_index i "
		"        WHERE i.indrelid = c.oid AND i.indisprimary) AS has_pk "
		"FROM pg_class c "
		"JOIN pg_namespace n ON c.relnamespace = n.oid "
		"WHERE c.relkind = 'r' "
		"AND n.nspname NOT LIKE 'pg\\_%' "
		"AND n.nspname NOT IN ('information_schema', 'dbbackup', "
		"'_timescaledb_internal', '_timescaledb_catalog', "
		"'_timescaledb_config', '_timescaledb_cache', "
		"'_timescaledb_functions', 'timescaledb_information', "
		"'timescaledb_experimental') "
		"AND c.oid NOT IN "
		"(SELECT d.objid FROM pg_depend d WHERE d.deptype = 'e') "
		"ORDER BY c.oid",
		true, 0);

	if (ret != SPI_OK_SELECT)
	{
		SPI_finish();
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("failed to enumerate user tables for logical FULL backup (rc=%d)",
						ret)));
	}

	for (i = 0; i < SPI_processed; i++)
	{
		char	   *nsp = SPI_getvalue(SPI_tuptable->vals[i],
									   SPI_tuptable->tupdesc, 1);
		char	   *rel = SPI_getvalue(SPI_tuptable->vals[i],
									   SPI_tuptable->tupdesc, 2);
		char	   *replident = SPI_getvalue(SPI_tuptable->vals[i],
											 SPI_tuptable->tupdesc, 3);
		char	   *has_pk = SPI_getvalue(SPI_tuptable->vals[i],
										   SPI_tuptable->tupdesc, 4);
		StringInfoData sql;

		if (nsp == NULL || rel == NULL)
			continue;

		if ((replident == NULL || replident[0] != 'f') &&
			(has_pk == NULL || has_pk[0] != 't'))
		{
			SPI_finish();
			ereport(ERROR,
					(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
					 errmsg("FULL logical PITR requires a primary key or REPLICA IDENTITY FULL"),
					 errdetail("Unsupported table: %s.%s", nsp, rel)));
		}

		initStringInfo(&sql);
		appendStringInfo(&sql, "LOCK TABLE %s.%s IN SHARE MODE",
						 quote_identifier(nsp), quote_identifier(rel));
		ret = SPI_execute(sql.data, false, 0);
		pfree(sql.data);

		if (ret != SPI_OK_UTILITY)
		{
			SPI_finish();
			ereport(ERROR,
					(errcode(ERRCODE_INTERNAL_ERROR),
					 errmsg("failed to lock %s.%s for logical FULL backup (rc=%d)",
							nsp, rel, ret)));
		}
	}

	SPI_finish();
}

static XLogRecPtr
create_logical_slot_for_db(const char *slot_name)
{
	int			ret;
	XLogRecPtr	lsn;

	SPI_connect();
	ret = SPI_execute_with_args(
		"SELECT lsn::text FROM "
		"pg_create_logical_replication_slot($1, 'pg_dbbackup', false, false, true)",
		1,
		(Oid[]){TEXTOID},
		(Datum[]){CStringGetTextDatum(slot_name)},
		NULL, false, 1);

	if (ret != SPI_OK_SELECT || SPI_processed != 1)
	{
		SPI_finish();
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not create logical replication slot \"%s\" (rc=%d)",
						slot_name, ret)));
	}

	if (!parse_lsn_text(SPI_getvalue(SPI_tuptable->vals[0],
									 SPI_tuptable->tupdesc, 1), &lsn))
	{
		SPI_finish();
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("could not parse logical slot start LSN")));
	}

	SPI_finish();
	return lsn;
}

static bool
lookup_logical_chain(Oid db_oid, char **slot_name_out,
					 XLogRecPtr *confirmed_lsn_out)
{
	int			ret;
	char	   *slot_text;
	char	   *lsn_text;
	MemoryContext caller_ctx = CurrentMemoryContext;

	if (slot_name_out)
		*slot_name_out = NULL;
	if (confirmed_lsn_out)
		*confirmed_lsn_out = InvalidXLogRecPtr;

	SPI_connect();
	ret = SPI_execute_with_args(
		"SELECT slot_name::text, confirmed_lsn::text "
		"FROM dbbackup.logical_chains "
		"WHERE db_oid = $1",
		1,
		(Oid[]){OIDOID},
		(Datum[]){ObjectIdGetDatum(db_oid)},
		NULL, true, 1);

	if (ret != SPI_OK_SELECT)
	{
		SPI_finish();
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not inspect logical PITR chain metadata (rc=%d)",
						ret)));
	}

	if (SPI_processed == 0)
	{
		SPI_finish();
		return false;
	}

	slot_text = SPI_getvalue(SPI_tuptable->vals[0],
							 SPI_tuptable->tupdesc, 1);
	lsn_text = SPI_getvalue(SPI_tuptable->vals[0],
							SPI_tuptable->tupdesc, 2);

	if (slot_text == NULL || lsn_text == NULL ||
		!parse_lsn_text(lsn_text, confirmed_lsn_out))
	{
		SPI_finish();
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("logical PITR chain metadata is corrupt")));
	}

	if (slot_name_out)
	{
		MemoryContext old_ctx = MemoryContextSwitchTo(caller_ctx);

		*slot_name_out = pstrdup(slot_text);
		MemoryContextSwitchTo(old_ctx);
	}

	SPI_finish();
	return true;
}

static char *
active_chain_slot_for_previous_backup(Oid db_oid, const char *db_name,
									  XLogRecPtr previous_stop_lsn,
									  const char *backup_type)
{
	char	   *slot_name;
	XLogRecPtr	confirmed_lsn;

	if (!lookup_logical_chain(db_oid, &slot_name, &confirmed_lsn))
		ereport(ERROR,
				(errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
				 errmsg("no active FULL logical PITR chain for database \"%s\"",
						db_name),
				 errhint("Take a FULL backup before taking a %s backup.",
						 backup_type)));

	if (confirmed_lsn != previous_stop_lsn)
	{
		char		expected[64];
		char		actual[64];

		format_lsn(previous_stop_lsn, expected, sizeof(expected));
		format_lsn(confirmed_lsn, actual, sizeof(actual));
		pfree(slot_name);
		ereport(ERROR,
				(errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
				 errmsg("previous backup does not match the active logical PITR chain"),
				 errdetail("Previous backup stops at %s, but the active chain is at %s.",
						   expected, actual),
				 errhint("Use the most recent backup in the chain, or take a new FULL backup.")));
	}

	/*
	 * Reconcile the slot's confirmed_flush_lsn with the chain endpoint.
	 * The chain row is written before pg_replication_slot_advance so a
	 * crash between them leaves slot.confirmed < chain.endpoint; advance
	 * the slot forward to close that gap. We must skip the advance if the
	 * slot is already at or past chain.endpoint, because
	 * pg_replication_slot_advance errors out (rather than no-op'ing) when
	 * asked to move backwards. A slot that is *ahead* of chain.endpoint
	 * only happens with a recreated slot or a pre-fix database; downstream
	 * checks (failover/invalidation/peek_changes) will then surface the
	 * real problem.
	 */
	{
		XLogRecPtr	slot_confirmed = slot_confirmed_flush_lsn(slot_name);

		if (!XLogRecPtrIsInvalid(slot_confirmed) &&
			slot_confirmed < confirmed_lsn)
			(void) advance_logical_slot_to_lsn(slot_name, confirmed_lsn);
	}

	return slot_name;
}

static void
ensure_logical_slot_failover_enabled(const char *slot_name)
{
	int			ret;
	bool		isnull;
	Datum		d;
	bool		failover;
	bool		temporary;
	char	   *invalidation_reason;

	ret = SPI_execute_with_args(
		"SELECT failover, temporary, invalidation_reason "
		"FROM pg_replication_slots "
		"WHERE slot_name = $1 "
		"  AND slot_type = 'logical' "
		"  AND plugin = 'pg_dbbackup' "
		"  AND database = current_database()",
		1,
		(Oid[]){TEXTOID},
		(Datum[]){CStringGetTextDatum(slot_name)},
		NULL, true, 1);

	if (ret != SPI_OK_SELECT)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("failed to inspect logical PITR slot \"%s\" (rc=%d)",
						slot_name, ret)));

	if (SPI_processed != 1)
		ereport(ERROR,
				(errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
				 errmsg("logical PITR slot \"%s\" does not exist", slot_name),
				 errdetail("The backup chain cannot continue without its database-scoped logical replication slot."),
				 errhint("Restore the failover-synchronized slot on the promoted primary, or take a new FULL backup.")));

	d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1,
					  &isnull);
	failover = !isnull && DatumGetBool(d);

	if (!failover)
		ereport(ERROR,
				(errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
				 errmsg("logical PITR slot \"%s\" is not failover-enabled",
						slot_name),
				 errdetail("pg_dbbackup FULL mode requires failover logical slots so the chain can continue after primary promotion."),
				 errhint("Take a new FULL backup with this pg_dbbackup version before relying on LOG or DIFFERENTIAL backups.")));

	d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 2,
					  &isnull);
	temporary = !isnull && DatumGetBool(d);
	if (temporary)
		ereport(ERROR,
				(errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
				 errmsg("logical PITR slot \"%s\" is temporary", slot_name),
				 errdetail("Temporary synced failover slots do not survive standby promotion."),
				 errhint("Wait until dbbackup.pg_dbbackup_failover_slot_ready() returns true on the candidate standby, or take a new FULL backup after promotion.")));

	invalidation_reason = SPI_getvalue(SPI_tuptable->vals[0],
									   SPI_tuptable->tupdesc, 3);
	if (invalidation_reason != NULL && invalidation_reason[0] != '\0')
		ereport(ERROR,
				(errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
				 errmsg("logical PITR slot \"%s\" is invalidated", slot_name),
				 errdetail("Invalidation reason: %s.", invalidation_reason),
				 errhint("Take a new FULL backup before continuing this backup chain.")));
}

static void
upsert_logical_chain(Oid db_oid, const char *db_name,
					 const char *slot_name, XLogRecPtr confirmed_lsn)
{
	int			ret;
	char		lsn_buf[64];

	snprintf(lsn_buf, sizeof(lsn_buf), "%X/%X",
			 (uint32) (confirmed_lsn >> 32), (uint32) confirmed_lsn);

	SPI_connect();
	ret = SPI_execute_with_args(
		"INSERT INTO dbbackup.logical_chains "
		"(db_oid, db_name, slot_name, confirmed_lsn, updated_at) "
		"VALUES ($1, $2, $3, $4::pg_lsn, clock_timestamp()) "
		"ON CONFLICT (db_oid) DO UPDATE SET "
		"db_name = EXCLUDED.db_name, "
		"slot_name = EXCLUDED.slot_name, "
		"confirmed_lsn = EXCLUDED.confirmed_lsn, "
		"updated_at = EXCLUDED.updated_at",
		4,
		(Oid[]){OIDOID, TEXTOID, TEXTOID, TEXTOID},
		(Datum[]){ObjectIdGetDatum(db_oid),
				  CStringGetTextDatum(db_name),
				  CStringGetTextDatum(slot_name),
				  CStringGetTextDatum(lsn_buf)},
		NULL, false, 1);
	SPI_finish();

	if (ret != SPI_OK_INSERT)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not update logical PITR chain metadata (rc=%d)",
						ret)));
}

static XLogRecPtr
emit_logical_flush_marker(bool switch_wal)
{
	int			ret;
	XLogRecPtr	lsn;

	SPI_connect();
	if (switch_wal)
	{
		ret = SPI_execute("SELECT pg_switch_wal()", false, 1);
		if (ret != SPI_OK_SELECT)
		{
			SPI_finish();
			ereport(ERROR,
					(errcode(ERRCODE_INTERNAL_ERROR),
					 errmsg("pg_switch_wal() failed before logical backup (rc=%d)",
							ret)));
		}
	}

	ret = SPI_execute(
		"SELECT pg_logical_emit_message(false, 'pg_dbbackup', '', true)::text",
		true, 1);

	if (ret != SPI_OK_SELECT || SPI_processed != 1)
	{
		SPI_finish();
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not emit logical backup flush marker (rc=%d)",
						ret)));
	}

	if (!parse_lsn_text(SPI_getvalue(SPI_tuptable->vals[0],
									 SPI_tuptable->tupdesc, 1), &lsn))
	{
		SPI_finish();
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("could not parse logical backup flush marker LSN")));
	}

	SPI_finish();
	return lsn;
}

static XLogRecPtr
slot_confirmed_flush_lsn(const char *slot_name)
{
	int			ret;
	XLogRecPtr	lsn = InvalidXLogRecPtr;
	char	   *lsn_text;

	SPI_connect();
	ret = SPI_execute_with_args(
		"SELECT confirmed_flush_lsn::text "
		"FROM pg_replication_slots "
		"WHERE slot_name = $1",
		1,
		(Oid[]){TEXTOID},
		(Datum[]){CStringGetTextDatum(slot_name)},
		NULL, true, 1);

	if (ret == SPI_OK_SELECT && SPI_processed == 1)
	{
		lsn_text = SPI_getvalue(SPI_tuptable->vals[0],
								SPI_tuptable->tupdesc, 1);
		if (lsn_text != NULL)
			(void) parse_lsn_text(lsn_text, &lsn);
	}

	SPI_finish();
	return lsn;
}

static XLogRecPtr
advance_logical_slot_to_lsn(const char *slot_name, XLogRecPtr upto_lsn)
{
	int			ret;
	XLogRecPtr	confirmed_lsn = InvalidXLogRecPtr;
	char	   *confirmed_text;
	char		upto_lsn_buf[64];

	format_lsn(upto_lsn, upto_lsn_buf, sizeof(upto_lsn_buf));

	SPI_connect();
	ret = SPI_execute_with_args(
		"SELECT end_lsn::text "
		"FROM pg_replication_slot_advance($1, $2::pg_lsn)",
		2,
		(Oid[]){TEXTOID, TEXTOID},
		(Datum[]){CStringGetTextDatum(slot_name),
				  CStringGetTextDatum(upto_lsn_buf)},
		NULL, false, 1);
	if (ret != SPI_OK_SELECT)
	{
		SPI_finish();
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("failed to advance logical PITR slot \"%s\" (rc=%d)",
						slot_name, ret)));
	}

	ret = SPI_execute_with_args(
		"SELECT confirmed_flush_lsn::text "
		"FROM pg_replication_slots "
		"WHERE slot_name = $1",
		1,
		(Oid[]){TEXTOID},
		(Datum[]){CStringGetTextDatum(slot_name)},
		NULL, true, 1);
	if (ret != SPI_OK_SELECT || SPI_processed != 1)
	{
		SPI_finish();
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("failed to inspect advanced logical PITR slot \"%s\" (rc=%d)",
						slot_name, ret)));
	}

	confirmed_text = SPI_getvalue(SPI_tuptable->vals[0],
								  SPI_tuptable->tupdesc, 1);
	if (confirmed_text != NULL)
		(void) parse_lsn_text(confirmed_text, &confirmed_lsn);

	SPI_finish();

	if (XLogRecPtrIsInvalid(confirmed_lsn) || confirmed_lsn < upto_lsn)
		return upto_lsn;
	return confirmed_lsn;
}

void
backup_full_finish_deferred_advance(Oid db_oid, const char *db_name,
									PgDbBackupDeferredAdvance *advance)
{
	if (advance == NULL || advance->slot_name == NULL ||
		XLogRecPtrIsInvalid(advance->stop_lsn))
		return;

	/*
	 * Order: upsert chain row, then advance slot, then mark slot retained.
	 * Both operations run inside the caller's transaction so the chain row
	 * insert is rolled back if any later step raises. pg_replication_slot_advance
	 * however persists slot state outside MVCC, so if it succeeds and a later
	 * step fails we'd be left with a moved slot but no chain row. To keep that
	 * state recoverable, drop_slot_on_abort stays true until the advance has
	 * succeeded: the PG_CATCH in the caller then drops the abandoned slot and
	 * the next FULL backup starts a fresh chain.
	 */
	upsert_logical_chain(db_oid, db_name, advance->slot_name,
						 advance->stop_lsn);
	advance->stop_lsn = advance_logical_slot_to_lsn(advance->slot_name,
													advance->stop_lsn);
	advance->drop_slot_on_abort = false;
	if (advance->reset_journal)
		pgdb_logical_journal_reset_state();
	ereport(LOG,
			(errmsg("pg_dbbackup: chain advanced for db \"%s\" slot \"%s\" to %X/%X (deferred)",
					db_name, advance->slot_name,
					LSN_FORMAT_ARGS(advance->stop_lsn))));
}

void
backup_full_abort_deferred_advance(PgDbBackupDeferredAdvance *advance)
{
	if (advance == NULL)
		return;

	if (advance->drop_slot_on_abort && advance->slot_name)
	{
		PG_TRY();
		{
			drop_replication_slot_if_exists(advance->slot_name);
		}
		PG_CATCH();
		{
			FlushErrorState();
		}
		PG_END_TRY();
	}
	backup_full_free_deferred_advance(advance);
}

void
backup_full_free_deferred_advance(PgDbBackupDeferredAdvance *advance)
{
	if (advance == NULL)
		return;

	if (advance->slot_name)
		pfree(advance->slot_name);
	advance->slot_name = NULL;
	advance->stop_lsn = InvalidXLogRecPtr;
	advance->reset_journal = false;
	advance->drop_slot_on_abort = false;
}


static uint32
write_logical_stream_section(BakFileWriter *writer, const char *slot_name,
							 XLogRecPtr upto_lsn)
{
	int			ret;
	uint64		i;
	uint32		frame_count = 0;
	char		lsn_buf[64];
	uint32		net_frame_count;

	format_lsn(upto_lsn, lsn_buf, sizeof(lsn_buf));

	SPI_connect();
	ensure_logical_slot_failover_enabled(slot_name);

	ret = SPI_execute_with_args(
		"SELECT data "
		"FROM pg_logical_slot_peek_changes($1, $2::pg_lsn, NULL)",
		2,
		(Oid[]){TEXTOID, TEXTOID},
		(Datum[]){CStringGetTextDatum(slot_name),
				  CStringGetTextDatum(lsn_buf)},
		NULL, false, 0);

	if (ret != SPI_OK_SELECT)
	{
		SPI_finish();
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("failed to decode logical PITR stream from slot \"%s\" (rc=%d)",
						slot_name,
						ret)));
	}

	for (i = 0; i < SPI_processed; i++)
	{
		if (SPI_getvalue(SPI_tuptable->vals[i],
						 SPI_tuptable->tupdesc, 1) != NULL)
			frame_count++;
	}
	net_frame_count = pg_hton32(frame_count);

	bakfile_begin_section(writer, BAKSECTION_LOGICAL_STREAM);
	bakfile_write_section_data(writer, &net_frame_count,
							   sizeof(net_frame_count));

	for (i = 0; i < SPI_processed; i++)
	{
		char	   *frame = SPI_getvalue(SPI_tuptable->vals[i],
										  SPI_tuptable->tupdesc, 1);
		uint32		len;
		uint32		net_len;

		if (frame == NULL)
			continue;

		len = (uint32) strlen(frame);
		net_len = pg_hton32(len);
		bakfile_write_section_data(writer, &net_len, sizeof(net_len));
		bakfile_write_section_data(writer, frame, len);
	}

	bakfile_end_section(writer);
	SPI_finish();

	return frame_count;
}

static void
backup_full_full_common(Oid db_oid, const char *db_name, const char *filepath,
						bool compress, const char *password,
						PgDbBackupDeferredAdvance *advance_out)
{
	FILE	   *probe;

	if (advance_out)
		memset(advance_out, 0, sizeof(*advance_out));

	if (!superuser())
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
				 errmsg("must be superuser to perform backups")));
	ensure_logical_journal_preloaded();
	validate_full_feature_matrix(db_oid);

	probe = AllocateFile(filepath, "wb");
	if (probe == NULL)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not open backup destination \"%s\": %m",
						filepath)));
	FreeFile(probe);
	(void) unlink(filepath);

	{
		char	   *logical_slot = NULL;
		XLogRecPtr	slot_lsn = InvalidXLogRecPtr;
		XLogRecPtr	chain_lsn;
		XLogRecPtr	existing_confirmed_lsn;
		bool		slot_created = false;

		PG_TRY();
		{
			pgdb_logical_journal_set_suppressed(true);
			lock_logical_full_tables(db_oid);

			if (!lookup_logical_chain(db_oid, &logical_slot,
									  &existing_confirmed_lsn) ||
				!logical_replication_slot_exists(logical_slot))
			{
				if (logical_slot)
					pfree(logical_slot);
				logical_slot = logical_slot_name_for_db(db_oid);
				drop_replication_slot_if_exists(logical_slot);
				slot_lsn = create_logical_slot_for_db(logical_slot);
				slot_created = true;
			}

			chain_lsn = emit_logical_flush_marker(false);
			if (!XLogRecPtrIsInvalid(slot_lsn) && chain_lsn < slot_lsn)
				chain_lsn = slot_lsn;
			backup_simple_full_as_mode_lsn(db_oid, db_name, filepath, compress,
										   password, BACKUP_MODE_FULL, chain_lsn);
			if (advance_out)
			{
				advance_out->slot_name = pstrdup(logical_slot);
				advance_out->stop_lsn = chain_lsn;
				advance_out->reset_journal = true;
				advance_out->drop_slot_on_abort = slot_created;
			}
			else
			{
				/*
				 * Order matters: write the chain row before advancing the
				 * slot. A crash after the upsert but before the advance
				 * leaves slot.confirmed < chain.endpoint; the next backup
				 * reconciles it via active_chain_slot_for_previous_backup.
				 * The reverse order would silently drop the chain row on
				 * crash and force the user to take a new FULL backup.
				 */
				upsert_logical_chain(db_oid, db_name, logical_slot, chain_lsn);
				chain_lsn = advance_logical_slot_to_lsn(logical_slot, chain_lsn);
				pgdb_logical_journal_reset_state();
				ereport(LOG,
						(errmsg("pg_dbbackup: chain established for db \"%s\" slot \"%s\" at %X/%X (full)",
								db_name, logical_slot,
								LSN_FORMAT_ARGS(chain_lsn))));
			}
		}
		PG_CATCH();
		{
			pgdb_logical_journal_set_suppressed(false);
			if (slot_created)
			{
				PG_TRY();
				{
					drop_replication_slot_if_exists(logical_slot);
				}
				PG_CATCH();
				{
					FlushErrorState();
				}
				PG_END_TRY();
			}
			if (logical_slot)
				pfree(logical_slot);
			PG_RE_THROW();
		}
		PG_END_TRY();
		pgdb_logical_journal_set_suppressed(false);
		if (logical_slot)
			pfree(logical_slot);
	}
	ereport(NOTICE,
			(errmsg("FULL backup of \"%s\" complete: logical snapshot written to \"%s\"",
					db_name, filepath)));
}

void
backup_full_full(Oid db_oid, const char *db_name, const char *filepath,
				 bool compress, const char *password)
{
	backup_full_full_common(db_oid, db_name, filepath, compress, password,
							NULL);
}

void
backup_full_full_deferred(Oid db_oid, const char *db_name,
						  const char *filepath, bool compress,
						  const char *password,
						  PgDbBackupDeferredAdvance *advance)
{
	if (advance == NULL)
		elog(ERROR, "deferred advance output is required");
	backup_full_full_common(db_oid, db_name, filepath, compress, password,
							advance);
}

static void
read_base_header(const char *base_filepath, const char *password,
				  const char *expected_db_name,
				  XLogRecPtr *base_stop_lsn_out,
				  PgDbBackupType *base_type_out)
{
	BakFileReader *reader;

	reader = bakfile_open(base_filepath, password);

	if (reader->header.mode != BACKUP_MODE_FULL)
	{
		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("base backup \"%s\" is not FULL mode", base_filepath)));
	}

	if (strcmp(reader->header.db_name, expected_db_name) != 0)
	{
		char	   *got = pstrdup(reader->header.db_name);

		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("base backup is for db \"%s\", not \"%s\"",
						got, expected_db_name)));
	}

	*base_stop_lsn_out = (XLogRecPtr) reader->header.stop_lsn;
	*base_type_out = reader->header.type;

	bakfile_close_reader(reader);
}

static void
backup_full_differential_common(Oid db_oid, const char *db_name,
								const char *filepath,
								const char *base_filepath, bool compress,
								const char *password,
								PgDbBackupDeferredAdvance *advance_out)
{
	BakFileHeader header;
	BakFileWriter *writer;
	StringInfo	metadata_sql;
	FILE	   *probe;
	char	   *slot_name;
	XLogRecPtr	base_stop_lsn = InvalidXLogRecPtr;
	PgDbBackupType base_type = BACKUP_TYPE_FULL;
	XLogRecPtr	current_lsn;
	TimeLineID	current_tli;
	TimestampTz diff_created_at;
	uint32		frame_count = 0;

	if (advance_out)
		memset(advance_out, 0, sizeof(*advance_out));

	if (!superuser())
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
				 errmsg("must be superuser to perform backups")));
	ensure_logical_journal_preloaded();
	validate_full_feature_matrix(db_oid);

	if (base_filepath == NULL || base_filepath[0] == '\0')
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("FULL differential backup requires base_filepath"),
				 errhint("Pass the path to the base FULL .bak via base_filepath := ...")));

	read_base_header(base_filepath, password, db_name, &base_stop_lsn,
					  &base_type);

	if (base_type != BACKUP_TYPE_FULL)
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("base for differential must be type=full, got a different type")));

	slot_name = active_chain_slot_for_previous_backup(db_oid, db_name,
													 base_stop_lsn,
													 "DIFFERENTIAL");

	probe = AllocateFile(filepath, "wb");
	if (probe == NULL)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not open backup destination \"%s\": %m",
						filepath)));
	FreeFile(probe);
	(void) unlink(filepath);

	LockSharedObject(DatabaseRelationId, db_oid, 0, AccessShareLock);

	PG_TRY();
	{
		XLogRecPtr	flush_marker_lsn;

		pgdb_logical_journal_set_suppressed(true);
		flush_marker_lsn = emit_logical_flush_marker(true);
		current_lsn = GetXLogWriteRecPtr();
		if (current_lsn < flush_marker_lsn)
			current_lsn = flush_marker_lsn;
		if (current_lsn < base_stop_lsn)
			current_lsn = base_stop_lsn;

		current_tli = GetWALInsertionTimeLine();
		diff_created_at = GetCurrentTimestamp();

		memset(&header, 0, sizeof(header));
		header.format_version = BAKFILE_VERSION;
		header.mode = BACKUP_MODE_FULL;
		header.type = BACKUP_TYPE_DIFFERENTIAL;
		strlcpy(header.db_name, db_name, sizeof(header.db_name));
		header.db_oid = db_oid;
		header.start_lsn = (uint64) base_stop_lsn;
		header.stop_lsn = (uint64) current_lsn;
		header.start_tli = current_tli;
		header.stop_tli = current_tli;
		header.pg_version = PG_VERSION_NUM;
		header.created_at = diff_created_at;
		header.base_backup_lsn = (uint64) base_stop_lsn;
		header.compressed = compress;
		header.encrypted = (password != NULL);
		header.file_count = 0;
		capture_db_xid_bounds(db_oid, &header.frozen_xid, &header.min_mxid);

		metadata_sql = metadata_gen_log_tail(db_oid);
		writer = bakfile_create(filepath, &header, compress, password);

		bakfile_begin_section(writer, BAKSECTION_METADATA);
		if (metadata_sql && metadata_sql->len > 0)
			bakfile_write_section_data(writer, metadata_sql->data,
									   metadata_sql->len);
		bakfile_end_section(writer);

		frame_count = write_logical_stream_section(writer, slot_name,
												   current_lsn);

		bakfile_close(writer);
		if (advance_out)
		{
			advance_out->slot_name = pstrdup(slot_name);
			advance_out->stop_lsn = current_lsn;
			advance_out->reset_journal = false;
			advance_out->drop_slot_on_abort = false;
		}
		else
		{
			/* See backup_full_finish_deferred_advance for ordering. */
			upsert_logical_chain(db_oid, db_name, slot_name, current_lsn);
			current_lsn = advance_logical_slot_to_lsn(slot_name, current_lsn);
			ereport(LOG,
					(errmsg("pg_dbbackup: chain advanced for db \"%s\" slot \"%s\" to %X/%X (differential)",
							db_name, slot_name,
							LSN_FORMAT_ARGS(current_lsn))));
		}
	}
	PG_CATCH();
	{
		pgdb_logical_journal_set_suppressed(false);
		pfree(slot_name);
		UnlockSharedObject(DatabaseRelationId, db_oid, 0, AccessShareLock);
		PG_RE_THROW();
	}
	PG_END_TRY();
	pgdb_logical_journal_set_suppressed(false);

	pfree(slot_name);
	UnlockSharedObject(DatabaseRelationId, db_oid, 0, AccessShareLock);

	ereport(NOTICE,
			(errmsg("FULL DIFFERENTIAL backup of \"%s\" complete: %u logical frame(s), LSN [%X/%X, %X/%X], written to \"%s\"",
					db_name,
					frame_count,
					LSN_FORMAT_ARGS(base_stop_lsn),
					LSN_FORMAT_ARGS(current_lsn),
					filepath)));
}

void
backup_full_differential(Oid db_oid, const char *db_name, const char *filepath,
						 const char *base_filepath, bool compress,
						 const char *password)
{
	backup_full_differential_common(db_oid, db_name, filepath, base_filepath,
									compress, password, NULL);
}

void
backup_full_differential_deferred(Oid db_oid, const char *db_name,
								  const char *filepath,
								  const char *base_filepath,
								  bool compress, const char *password,
								  PgDbBackupDeferredAdvance *advance)
{
	if (advance == NULL)
		elog(ERROR, "deferred advance output is required");
	backup_full_differential_common(db_oid, db_name, filepath, base_filepath,
									compress, password, advance);
}

static void
read_prev_backup_info(const char *prev_filepath, const char *password,
					  const char *expected_db_name,
					  XLogRecPtr *prev_stop_out,
					  TimestampTz *prev_created_at_out)
{
	BakFileReader *reader;
	XLogRecPtr	prev_stop;
	TimestampTz prev_created_at;

	reader = bakfile_open(prev_filepath, password);

	if (reader->header.mode != BACKUP_MODE_FULL)
	{
		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("previous backup \"%s\" is not FULL mode",
						prev_filepath)));
	}

	if (strcmp(reader->header.db_name, expected_db_name) != 0)
	{
		char	   *got = pstrdup(reader->header.db_name);

		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("previous backup is for db \"%s\", not \"%s\"",
						got, expected_db_name)));
	}

	prev_stop = (XLogRecPtr) reader->header.stop_lsn;
	prev_created_at = (TimestampTz) reader->header.created_at;
	bakfile_close_reader(reader);

	*prev_stop_out = prev_stop;
	*prev_created_at_out = prev_created_at;
}

static void
backup_full_log_common(Oid db_oid, const char *db_name, const char *filepath,
					   const char *prev_filepath, bool compress,
					   const char *password,
					   PgDbBackupDeferredAdvance *advance_out)
{
	BakFileHeader header;
	BakFileWriter *writer;
	FILE	   *probe;
	XLogRecPtr	prev_stop_lsn;
	TimestampTz prev_created_at;
	XLogRecPtr	current_lsn;
	TimeLineID	current_tli;
	TimestampTz log_created_at;
	uint32		frame_count = 0;
	char	   *logical_slot;
	StringInfo	metadata_sql;

	if (advance_out)
		memset(advance_out, 0, sizeof(*advance_out));

	if (!superuser())
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
				 errmsg("must be superuser to perform backups")));
	ensure_logical_journal_preloaded();
	validate_full_feature_matrix(db_oid);

	if (prev_filepath == NULL || prev_filepath[0] == '\0')
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("FULL log backup requires base_filepath (the previous .bak)"),
				 errhint("Pass the most recent FULL/DIFF/LOG .bak via base_filepath := ...")));

	read_prev_backup_info(prev_filepath, password, db_name,
						  &prev_stop_lsn, &prev_created_at);
	(void) prev_created_at;
	logical_slot = active_chain_slot_for_previous_backup(db_oid, db_name,
														prev_stop_lsn,
														"LOG");

	probe = AllocateFile(filepath, "wb");
	if (probe == NULL)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not open backup destination \"%s\": %m",
						filepath)));
	FreeFile(probe);
	(void) unlink(filepath);

	LockSharedObject(DatabaseRelationId, db_oid, 0, AccessShareLock);

	PG_TRY();
	{
		XLogRecPtr	flush_marker_lsn;

		pgdb_logical_journal_set_suppressed(true);
		flush_marker_lsn = emit_logical_flush_marker(true);
		current_lsn = GetXLogWriteRecPtr();
		if (current_lsn < flush_marker_lsn)
			current_lsn = flush_marker_lsn;
		current_tli = GetWALInsertionTimeLine();
		log_created_at = GetCurrentTimestamp();

		if (current_lsn < prev_stop_lsn)
			current_lsn = prev_stop_lsn;

		memset(&header, 0, sizeof(header));
		header.format_version = BAKFILE_VERSION;
		header.mode = BACKUP_MODE_FULL;
		header.type = BACKUP_TYPE_LOG;
		strlcpy(header.db_name, db_name, sizeof(header.db_name));
		header.db_oid = db_oid;
		header.start_lsn = (uint64) prev_stop_lsn;
		header.stop_lsn = (uint64) current_lsn;
		header.start_tli = current_tli;
		header.stop_tli = current_tli;
		header.pg_version = PG_VERSION_NUM;
		header.created_at = log_created_at;
		header.base_backup_lsn = (uint64) prev_stop_lsn;
		header.compressed = compress;
		header.encrypted = (password != NULL);
		header.file_count = 0;
		capture_db_xid_bounds(db_oid, &header.frozen_xid, &header.min_mxid);

		metadata_sql = metadata_gen_log_tail(db_oid);
		writer = bakfile_create(filepath, &header, compress, password);

		bakfile_begin_section(writer, BAKSECTION_METADATA);
		if (metadata_sql && metadata_sql->len > 0)
			bakfile_write_section_data(writer, metadata_sql->data,
									   metadata_sql->len);
		bakfile_end_section(writer);

		frame_count = write_logical_stream_section(writer, logical_slot,
												   current_lsn);

		bakfile_close(writer);
		if (advance_out)
		{
			advance_out->slot_name = pstrdup(logical_slot);
			advance_out->stop_lsn = current_lsn;
			advance_out->reset_journal = false;
			advance_out->drop_slot_on_abort = false;
		}
		else
		{
			/* See backup_full_finish_deferred_advance for ordering. */
			upsert_logical_chain(db_oid, db_name, logical_slot, current_lsn);
			current_lsn = advance_logical_slot_to_lsn(logical_slot, current_lsn);
			ereport(LOG,
					(errmsg("pg_dbbackup: chain advanced for db \"%s\" slot \"%s\" to %X/%X (log)",
							db_name, logical_slot,
							LSN_FORMAT_ARGS(current_lsn))));
		}
	}
	PG_CATCH();
	{
		pgdb_logical_journal_set_suppressed(false);
		pfree(logical_slot);
		UnlockSharedObject(DatabaseRelationId, db_oid, 0, AccessShareLock);
		PG_RE_THROW();
	}
	PG_END_TRY();
	pgdb_logical_journal_set_suppressed(false);

	pfree(logical_slot);
	UnlockSharedObject(DatabaseRelationId, db_oid, 0, AccessShareLock);

	ereport(NOTICE,
			(errmsg("FULL LOG backup of \"%s\" complete: %u logical frame(s), LSN [%X/%X, %X/%X], written to \"%s\"",
					db_name,
					frame_count,
					LSN_FORMAT_ARGS(prev_stop_lsn),
					LSN_FORMAT_ARGS(current_lsn),
					filepath)));
}

void
backup_full_log(Oid db_oid, const char *db_name, const char *filepath,
				const char *prev_filepath, bool compress, const char *password)
{
	backup_full_log_common(db_oid, db_name, filepath, prev_filepath,
						   compress, password, NULL);
}

void
backup_full_log_deferred(Oid db_oid, const char *db_name,
						 const char *filepath, const char *prev_filepath,
						 bool compress, const char *password,
						 PgDbBackupDeferredAdvance *advance)
{
	if (advance == NULL)
		elog(ERROR, "deferred advance output is required");
	backup_full_log_common(db_oid, db_name, filepath, prev_filepath,
						   compress, password, advance);
}
