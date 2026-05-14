#include "postgres.h"

#include "access/genam.h"
#include "access/htup_details.h"
#include "access/skey.h"
#include "access/table.h"
#include "catalog/indexing.h"
#include "catalog/pg_class.h"
#include "catalog/pg_inherits.h"
#include "catalog/namespace.h"
#include "catalog/pg_type.h"
#include "nodes/bitmapset.h"
#include "replication/logical.h"
#include "replication/output_plugin.h"
#include "replication/reorderbuffer.h"
#include "utils/builtins.h"
#include "utils/fmgroids.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"
#include "utils/rel.h"
#include "utils/relcache.h"
#include "utils/timestamp.h"

typedef struct PgDbBackupDecodingData
{
	MemoryContext context;
} PgDbBackupDecodingData;

typedef struct PgDbBackupTxnData
{
	bool		wrote_changes;
} PgDbBackupTxnData;

#if PG_VERSION_NUM < 190000
#define PGDB_TXN_COMMIT_TIME(txn) ((txn)->xact_time.commit_time)
#else
#define PGDB_TXN_COMMIT_TIME(txn) ((txn)->commit_time)
#endif

static void pgdb_decode_startup(LogicalDecodingContext *ctx,
								OutputPluginOptions *opt, bool is_init);
static void pgdb_decode_shutdown(LogicalDecodingContext *ctx);
static void pgdb_decode_begin(LogicalDecodingContext *ctx,
							  ReorderBufferTXN *txn);
static void pgdb_decode_commit(LogicalDecodingContext *ctx,
							   ReorderBufferTXN *txn, XLogRecPtr commit_lsn);
static void pgdb_decode_message(LogicalDecodingContext *ctx,
								ReorderBufferTXN *txn,
								XLogRecPtr message_lsn,
								bool transactional,
								const char *prefix,
								Size message_size,
								const char *message);
static void pgdb_decode_change(LogicalDecodingContext *ctx,
							   ReorderBufferTXN *txn, Relation relation,
							   ReorderBufferChange *change);
static void pgdb_decode_truncate(LogicalDecodingContext *ctx,
								 ReorderBufferTXN *txn, int nrelations,
								 Relation relations[],
								 ReorderBufferChange *change);

void
_PG_output_plugin_init(OutputPluginCallbacks *cb)
{
	cb->startup_cb = pgdb_decode_startup;
	cb->begin_cb = pgdb_decode_begin;
	cb->change_cb = pgdb_decode_change;
	cb->truncate_cb = pgdb_decode_truncate;
	cb->commit_cb = pgdb_decode_commit;
	cb->message_cb = pgdb_decode_message;
	cb->shutdown_cb = pgdb_decode_shutdown;
}

static void
pgdb_decode_startup(LogicalDecodingContext *ctx,
					OutputPluginOptions *opt, bool is_init)
{
	PgDbBackupDecodingData *data;

	data = palloc0_object(PgDbBackupDecodingData);
	data->context = AllocSetContextCreate(ctx->context,
										  "pg_dbbackup logical decoding",
										  ALLOCSET_DEFAULT_SIZES);
	ctx->output_plugin_private = data;

	opt->output_type = OUTPUT_PLUGIN_TEXTUAL_OUTPUT;
	opt->receive_rewrites = false;
}

static void
pgdb_decode_shutdown(LogicalDecodingContext *ctx)
{
	PgDbBackupDecodingData *data = ctx->output_plugin_private;

	if (data != NULL && data->context != NULL)
		MemoryContextDelete(data->context);
}

static void
pgdb_decode_begin(LogicalDecodingContext *ctx, ReorderBufferTXN *txn)
{
	PgDbBackupTxnData *txndata;

	txndata = MemoryContextAllocZero(ctx->context, sizeof(PgDbBackupTxnData));
	txn->output_plugin_private = txndata;
}

static void
pgdb_output_begin_if_needed(LogicalDecodingContext *ctx, ReorderBufferTXN *txn)
{
	PgDbBackupTxnData *txndata = txn->output_plugin_private;

	if (txndata != NULL && txndata->wrote_changes)
		return;

	OutputPluginPrepareWrite(ctx, true);
	appendStringInfo(ctx->out, "BEGIN\t%u", txn->xid);
	OutputPluginWrite(ctx, true);

	if (txndata != NULL)
		txndata->wrote_changes = true;
}

static void
pgdb_decode_commit(LogicalDecodingContext *ctx, ReorderBufferTXN *txn,
				   XLogRecPtr commit_lsn)
{
	PgDbBackupTxnData *txndata = txn->output_plugin_private;

	if (txndata == NULL || !txndata->wrote_changes)
	{
		txn->output_plugin_private = NULL;
		return;
	}

	OutputPluginPrepareWrite(ctx, true);
	appendStringInfo(ctx->out, "COMMIT\t" INT64_FORMAT "\t%X/%X",
					 (int64) PGDB_TXN_COMMIT_TIME(txn),
					 (uint32) (commit_lsn >> 32), (uint32) commit_lsn);
	OutputPluginWrite(ctx, true);

	txn->output_plugin_private = NULL;
}

static void
pgdb_decode_message(LogicalDecodingContext *ctx, ReorderBufferTXN *txn,
					XLogRecPtr message_lsn, bool transactional,
					const char *prefix, Size message_size,
					const char *message)
{
	if (prefix == NULL || strcmp(prefix, "pg_dbbackup") != 0)
		return;

	OutputPluginPrepareWrite(ctx, true);
	appendStringInfoString(ctx->out, "NOOP");
	OutputPluginWrite(ctx, true);
}

static void
append_quoted_literal(StringInfo s, Oid typid, int32 typmod,
					  Datum value, bool isnull)
{
	Oid			typoutput;
	bool		typisvarlena;
	char	   *out;
	const char *p;

	if (isnull)
	{
		appendStringInfoString(s, "NULL");
		return;
	}

	getTypeOutputInfo(typid, &typoutput, &typisvarlena);

	if (typisvarlena)
		value = PointerGetDatum(PG_DETOAST_DATUM(value));

	out = OidOutputFunctionCall(typoutput, value);

	appendStringInfoChar(s, '\'');
	for (p = out; *p; p++)
	{
		if (SQL_STR_DOUBLE(*p, false))
			appendStringInfoChar(s, *p);
		appendStringInfoChar(s, *p);
	}
	appendStringInfoChar(s, '\'');
	appendStringInfo(s, "::%s", format_type_with_typemod(typid, typmod));
}

static bool
attr_is_replay_writable(Form_pg_attribute attr)
{
	return !attr->attisdropped &&
		attr->attnum > 0 &&
		attr->attgenerated == '\0';
}

static void
append_column_list(StringInfo s, TupleDesc tupdesc)
{
	bool		first = true;
	int			natt;

	for (natt = 0; natt < tupdesc->natts; natt++)
	{
		Form_pg_attribute attr = TupleDescAttr(tupdesc, natt);

		if (!attr_is_replay_writable(attr))
			continue;

		if (!first)
			appendStringInfoString(s, ", ");
		appendStringInfoString(s, quote_identifier(NameStr(attr->attname)));
		first = false;
	}
}

static void
append_tuple_values(StringInfo s, TupleDesc tupdesc, HeapTuple tuple)
{
	bool		first = true;
	int			natt;

	for (natt = 0; natt < tupdesc->natts; natt++)
	{
		Form_pg_attribute attr = TupleDescAttr(tupdesc, natt);
		bool		isnull;
		Datum		value;

		if (!attr_is_replay_writable(attr))
			continue;

		value = heap_getattr(tuple, natt + 1, tupdesc, &isnull);

		if (!first)
			appendStringInfoString(s, ", ");
		append_quoted_literal(s, attr->atttypid, attr->atttypmod, value, isnull);
		first = false;
	}
}

static void
append_tuple_assignments(StringInfo s, TupleDesc tupdesc, HeapTuple tuple)
{
	bool		first = true;
	int			natt;

	for (natt = 0; natt < tupdesc->natts; natt++)
	{
		Form_pg_attribute attr = TupleDescAttr(tupdesc, natt);
		bool		isnull;
		Datum		value;

		if (!attr_is_replay_writable(attr))
			continue;

		value = heap_getattr(tuple, natt + 1, tupdesc, &isnull);

		if (!first)
			appendStringInfoString(s, ", ");
		appendStringInfoString(s, quote_identifier(NameStr(attr->attname)));
		appendStringInfoString(s, " = ");
		append_quoted_literal(s, attr->atttypid, attr->atttypmod, value, isnull);
		first = false;
	}
}

static void
append_tuple_predicate(StringInfo s, TupleDesc tupdesc, HeapTuple tuple,
					   Bitmapset *keyattrs, bool all_attrs)
{
	bool		first = true;
	int			natt;

	for (natt = 0; natt < tupdesc->natts; natt++)
	{
		Form_pg_attribute attr = TupleDescAttr(tupdesc, natt);
		bool		isnull;
		Datum		value;

		if (attr->attisdropped || attr->attnum < 0)
			continue;
		if (!all_attrs &&
			!bms_is_member(attr->attnum - FirstLowInvalidHeapAttributeNumber,
						   keyattrs))
			continue;

		value = heap_getattr(tuple, natt + 1, tupdesc, &isnull);

		if (!first)
			appendStringInfoString(s, " AND ");
		appendStringInfoString(s, quote_identifier(NameStr(attr->attname)));
		appendStringInfoString(s, " IS NOT DISTINCT FROM ");
		append_quoted_literal(s, attr->atttypid, attr->atttypmod, value, isnull);
		first = false;
	}

	if (first)
		appendStringInfoString(s, "false");
}

static Bitmapset *
relation_key_attrs(Relation relation, bool *all_attrs)
{
	*all_attrs =
		RelationGetForm(relation)->relreplident == REPLICA_IDENTITY_FULL;
	if (*all_attrs)
		return NULL;

	return RelationGetIdentityKeyBitmap(relation);
}

static const char *
qualified_relation_name(Relation relation)
{
	Form_pg_class class_form = RelationGetForm(relation);

	return quote_qualified_identifier(
		get_namespace_name(get_rel_namespace(RelationGetRelid(relation))),
		class_form->relrewrite ? get_rel_name(class_form->relrewrite) :
		NameStr(class_form->relname));
}

static bool
timescaledb_internal_schema(const char *nspname)
{
	return strcmp(nspname, "_timescaledb_internal") == 0 ||
		strcmp(nspname, "_timescaledb_catalog") == 0 ||
		strcmp(nspname, "_timescaledb_config") == 0 ||
		strcmp(nspname, "_timescaledb_cache") == 0 ||
		strcmp(nspname, "_timescaledb_functions") == 0 ||
		strcmp(nspname, "timescaledb_information") == 0 ||
		strcmp(nspname, "timescaledb_experimental") == 0;
}

static char *
timescaledb_chunk_hypertable_name(Relation relation)
{
	Relation	inhrel;
	ScanKeyData key;
	SysScanDesc scan;
	HeapTuple	tuple;
	Oid			parent_oid = InvalidOid;
	char	   *schema_name;
	char	   *table_name;
	char	   *result = NULL;

	inhrel = table_open(InheritsRelationId, AccessShareLock);
	ScanKeyInit(&key,
				Anum_pg_inherits_inhrelid,
				BTEqualStrategyNumber,
				F_OIDEQ,
				ObjectIdGetDatum(RelationGetRelid(relation)));
	scan = systable_beginscan(inhrel,
							  InheritsRelidSeqnoIndexId,
							  true,
							  NULL,
							  1,
							  &key);
	tuple = systable_getnext(scan);
	if (HeapTupleIsValid(tuple))
	{
		Form_pg_inherits inh = (Form_pg_inherits) GETSTRUCT(tuple);

		parent_oid = inh->inhparent;
	}
	systable_endscan(scan);
	table_close(inhrel, AccessShareLock);

	if (!OidIsValid(parent_oid))
		return NULL;

	schema_name = get_namespace_name(get_rel_namespace(parent_oid));
	table_name = get_rel_name(parent_oid);
	if (schema_name == NULL || table_name == NULL)
		return NULL;

	result = pstrdup(quote_qualified_identifier(schema_name, table_name));
	pfree(schema_name);
	return result;
}

static char *
logical_relation_name(Relation relation)
{
	char	   *nspname;
	char	   *result;

	nspname = get_namespace_name(get_rel_namespace(RelationGetRelid(relation)));
	if (nspname == NULL)
		return NULL;

	if (strncmp(nspname, "pg_", 3) == 0 ||
		strcmp(nspname, "information_schema") == 0 ||
		strcmp(nspname, "dbbackup") == 0)
	{
		pfree(nspname);
		return NULL;
	}

	if (timescaledb_internal_schema(nspname))
	{
		if (strcmp(nspname, "_timescaledb_internal") == 0)
		{
			result = timescaledb_chunk_hypertable_name(relation);
			pfree(nspname);
			return result;
		}

		pfree(nspname);
		return NULL;
	}

	pfree(nspname);
	return pstrdup(qualified_relation_name(relation));
}

static bool
relation_is_dbbackup_table(Relation relation, const char *table_name)
{
	char	   *nspname;
	const char *relname;
	bool		result;

	nspname = get_namespace_name(get_rel_namespace(RelationGetRelid(relation)));
	relname = RelationGetRelationName(relation);
	result = nspname != NULL &&
		strcmp(nspname, "dbbackup") == 0 &&
		strcmp(relname, table_name) == 0;
	if (nspname)
		pfree(nspname);
	return result;
}

static char *
tuple_attr_output_by_name(TupleDesc tupdesc, HeapTuple tuple, const char *name)
{
	int			natt;

	for (natt = 0; natt < tupdesc->natts; natt++)
	{
		Form_pg_attribute attr = TupleDescAttr(tupdesc, natt);
		bool		isnull;
		Datum		value;
		Oid			typoutput;
		bool		typisvarlena;

		if (attr->attisdropped || attr->attnum < 0)
			continue;
		if (strcmp(NameStr(attr->attname), name) != 0)
			continue;

		value = heap_getattr(tuple, natt + 1, tupdesc, &isnull);
		if (isnull)
			return NULL;

		getTypeOutputInfo(attr->atttypid, &typoutput, &typisvarlena);
		if (typisvarlena)
			value = PointerGetDatum(PG_DETOAST_DATUM(value));
		return OidOutputFunctionCall(typoutput, value);
	}

	return NULL;
}

static void
pgdb_decode_ddl_log_insert(LogicalDecodingContext *ctx,
						   ReorderBufferTXN *txn,
						   TupleDesc tupdesc,
						   HeapTuple tuple)
{
	char	   *command;
	size_t		len;

	command = tuple_attr_output_by_name(tupdesc, tuple, "command");
	if (command == NULL || command[0] == '\0')
		return;

	pgdb_output_begin_if_needed(ctx, txn);
	OutputPluginPrepareWrite(ctx, true);
	appendStringInfoString(ctx->out,
						   "SQL\tSET LOCAL dbbackup.replaying = 'on';\n");
	appendStringInfoString(ctx->out, command);
	len = strlen(command);
	if (len == 0 || command[len - 1] != ';')
		appendStringInfoChar(ctx->out, ';');
	OutputPluginWrite(ctx, true);
}

static void
pgdb_decode_sequence_log_insert(LogicalDecodingContext *ctx,
								ReorderBufferTXN *txn,
								TupleDesc tupdesc,
								HeapTuple tuple)
{
	char	   *schema_name;
	char	   *sequence_name;
	char	   *last_value;
	char	   *is_called;
	char	   *qualified;

	schema_name = tuple_attr_output_by_name(tupdesc, tuple, "schema_name");
	sequence_name = tuple_attr_output_by_name(tupdesc, tuple, "sequence_name");
	last_value = tuple_attr_output_by_name(tupdesc, tuple, "last_value");
	is_called = tuple_attr_output_by_name(tupdesc, tuple, "is_called");

	if (schema_name == NULL || sequence_name == NULL ||
		last_value == NULL || is_called == NULL)
		return;

	qualified = quote_qualified_identifier(schema_name, sequence_name);

	pgdb_output_begin_if_needed(ctx, txn);
	OutputPluginPrepareWrite(ctx, true);
	appendStringInfo(ctx->out,
					 "SQL\tSELECT pg_catalog.setval(%s::regclass, %s, %s);",
					 quote_literal_cstr(qualified),
					 last_value,
					 (strcmp(is_called, "t") == 0 ||
					  pg_strcasecmp(is_called, "true") == 0) ?
					 "true" : "false");
	OutputPluginWrite(ctx, true);
}

static void
pgdb_decode_large_object_log_insert(LogicalDecodingContext *ctx,
									ReorderBufferTXN *txn,
									TupleDesc tupdesc,
									HeapTuple tuple)
{
	char	   *snapshot_sql;

	snapshot_sql = tuple_attr_output_by_name(tupdesc, tuple, "snapshot_sql");
	if (snapshot_sql == NULL || snapshot_sql[0] == '\0')
		return;

	pgdb_output_begin_if_needed(ctx, txn);
	OutputPluginPrepareWrite(ctx, true);
	appendStringInfoString(ctx->out,
						   "SQL\tSET LOCAL dbbackup.replaying = 'on';\n");
	appendStringInfoString(ctx->out, snapshot_sql);
	OutputPluginWrite(ctx, true);
}

static void
pgdb_decode_noop_change(LogicalDecodingContext *ctx, ReorderBufferTXN *txn)
{
	pgdb_output_begin_if_needed(ctx, txn);
	OutputPluginPrepareWrite(ctx, true);
	appendStringInfoString(ctx->out, "NOOP");
	OutputPluginWrite(ctx, true);
}

static void
pgdb_decode_change(LogicalDecodingContext *ctx, ReorderBufferTXN *txn,
				   Relation relation, ReorderBufferChange *change)
{
	TupleDesc	tupdesc = RelationGetDescr(relation);
	char	   *relname;

	if (relation_is_dbbackup_table(relation, "logical_chains"))
	{
		pgdb_decode_noop_change(ctx, txn);
		return;
	}
	if (relation_is_dbbackup_table(relation, "ddl_log"))
	{
		if (change->action == REORDER_BUFFER_CHANGE_INSERT &&
			change->data.tp.newtuple != NULL)
			pgdb_decode_ddl_log_insert(ctx, txn, tupdesc,
									   change->data.tp.newtuple);
		return;
	}
	if (relation_is_dbbackup_table(relation, "sequence_log"))
	{
		if (change->action == REORDER_BUFFER_CHANGE_INSERT &&
			change->data.tp.newtuple != NULL)
			pgdb_decode_sequence_log_insert(ctx, txn, tupdesc,
											change->data.tp.newtuple);
		return;
	}
	if (relation_is_dbbackup_table(relation, "large_object_log"))
	{
		if (change->action == REORDER_BUFFER_CHANGE_INSERT &&
			change->data.tp.newtuple != NULL)
			pgdb_decode_large_object_log_insert(ctx, txn, tupdesc,
												change->data.tp.newtuple);
		return;
	}

	relname = logical_relation_name(relation);
	if (relname == NULL)
		return;

	pgdb_output_begin_if_needed(ctx, txn);
	OutputPluginPrepareWrite(ctx, true);

	switch (change->action)
	{
		case REORDER_BUFFER_CHANGE_INSERT:
			if (change->data.tp.newtuple == NULL)
				ereport(ERROR,
						(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
						 errmsg("logical INSERT for %s did not include tuple data",
								relname)));
			appendStringInfo(ctx->out, "SQL\tINSERT INTO %s (",
							 relname);
			append_column_list(ctx->out, tupdesc);
			appendStringInfoString(ctx->out, ") OVERRIDING SYSTEM VALUE VALUES (");
			append_tuple_values(ctx->out, tupdesc, change->data.tp.newtuple);
			appendStringInfoString(ctx->out, ");");
			break;

		case REORDER_BUFFER_CHANGE_UPDATE:
			{
				HeapTuple	predtuple;
				Bitmapset  *keyattrs;
				bool		all_attrs;

				if (change->data.tp.newtuple == NULL)
					ereport(ERROR,
							(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
							 errmsg("logical UPDATE for %s did not include tuple data",
									relname)));

				keyattrs = relation_key_attrs(relation, &all_attrs);
				if (!all_attrs && keyattrs == NULL)
					ereport(ERROR,
							(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
							 errmsg("logical UPDATE for %s lacks replica identity",
									relname),
							 errhint("Use a primary key or REPLICA IDENTITY FULL for FULL-mode PITR tables.")));

				predtuple = change->data.tp.oldtuple != NULL ?
					change->data.tp.oldtuple : change->data.tp.newtuple;

				appendStringInfo(ctx->out, "SQL\tUPDATE %s SET ", relname);
				append_tuple_assignments(ctx->out, tupdesc,
										 change->data.tp.newtuple);
				appendStringInfoString(ctx->out, " WHERE ");
				append_tuple_predicate(ctx->out, tupdesc, predtuple,
									   keyattrs, all_attrs);
				appendStringInfoString(ctx->out, ";");
				bms_free(keyattrs);
			}
			break;

		case REORDER_BUFFER_CHANGE_DELETE:
			if (change->data.tp.oldtuple == NULL)
				ereport(ERROR,
						(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
						 errmsg("logical DELETE for %s lacks replica identity",
								relname),
						 errhint("Use a primary key or REPLICA IDENTITY FULL for FULL-mode PITR tables.")));
			{
				Bitmapset  *keyattrs;
				bool		all_attrs;

				keyattrs = relation_key_attrs(relation, &all_attrs);
				if (!all_attrs && keyattrs == NULL)
					ereport(ERROR,
							(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
							 errmsg("logical DELETE for %s lacks replica identity",
									relname),
							 errhint("Use a primary key or REPLICA IDENTITY FULL for FULL-mode PITR tables.")));

				appendStringInfo(ctx->out, "SQL\tDELETE FROM %s WHERE ",
								 relname);
				append_tuple_predicate(ctx->out, tupdesc,
									   change->data.tp.oldtuple,
									   keyattrs, all_attrs);
				appendStringInfoString(ctx->out, ";");
				bms_free(keyattrs);
			}
			break;

		default:
			ereport(ERROR,
					(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
					 errmsg("unsupported logical change type")));
	}

	OutputPluginWrite(ctx, true);
}

static void
pgdb_decode_truncate(LogicalDecodingContext *ctx, ReorderBufferTXN *txn,
					 int nrelations, Relation relations[],
					 ReorderBufferChange *change)
{
	StringInfoData relnames;
	int			i;
	int			included = 0;

	initStringInfo(&relnames);

	for (i = 0; i < nrelations; i++)
	{
		char	   *relname = logical_relation_name(relations[i]);

		if (relname == NULL)
			continue;

		if (included > 0)
			appendStringInfoString(&relnames, ", ");
		appendStringInfoString(&relnames, relname);
		included++;
	}
	if (included == 0)
	{
		pfree(relnames.data);
		return;
	}

	pgdb_output_begin_if_needed(ctx, txn);
	OutputPluginPrepareWrite(ctx, true);
	appendStringInfoString(ctx->out, "SQL\tTRUNCATE TABLE ");
	appendStringInfoString(ctx->out, relnames.data);
	if (change->data.truncate.restart_seqs)
		appendStringInfoString(ctx->out, " RESTART IDENTITY");
	if (change->data.truncate.cascade)
		appendStringInfoString(ctx->out, " CASCADE");
	appendStringInfoString(ctx->out, ";");

	OutputPluginWrite(ctx, true);
	pfree(relnames.data);
}
