#include "postgres.h"
#include "fmgr.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "access/htup_details.h"
#include "utils/builtins.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"
#include "utils/timestamp.h"

#include "inspect.h"
#include "bakfile.h"

static const char *
backup_type_to_text(PgDbBackupType type)
{
	switch (type)
	{
		case BACKUP_TYPE_FULL:
			return "full";
		case BACKUP_TYPE_DIFFERENTIAL:
			return "differential";
		case BACKUP_TYPE_LOG:
			return "log";
	}
	return "unknown";
}

static const char *
backup_mode_to_text(PgDbBackupMode mode)
{
	return (mode == BACKUP_MODE_FULL) ? "full" : "simple";
}

static char *
hex_encode_digest(const uint8 *digest, size_t len)
{
	static const char hex[] = "0123456789abcdef";
	char	   *out = palloc(len * 2 + 1);
	size_t		i;

	for (i = 0; i < len; i++)
	{
		out[i * 2] = hex[(digest[i] >> 4) & 0xF];
		out[i * 2 + 1] = hex[digest[i] & 0xF];
	}
	out[len * 2] = '\0';
	return out;
}

Datum
inspect_header(FunctionCallInfo fcinfo)
{
	text	   *filepath_text = PG_GETARG_TEXT_PP(0);
	char	   *filepath = text_to_cstring(filepath_text);
	BakFileReader *reader;
	BakFileHeader *hdr;
	TupleDesc	tupdesc;
	Datum		values[11];
	bool		nulls[11];
	HeapTuple	tuple;
	TimestampTz	created_at_tz;

	reader = bakfile_open(filepath, NULL);
	hdr = &reader->header;

	if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE)
	{
		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
				 errmsg("function returning record called in context that cannot accept type record")));
	}

	tupdesc = BlessTupleDesc(tupdesc);

	memset(nulls, 0, sizeof(nulls));

	values[0] = CStringGetTextDatum(backup_type_to_text(hdr->type));
	values[1] = CStringGetTextDatum(backup_mode_to_text(hdr->mode));
	values[2] = CStringGetTextDatum(hdr->db_name);
	values[3] = ObjectIdGetDatum(hdr->db_oid);

	created_at_tz = (TimestampTz) hdr->created_at;
	values[4] = TimestampTzGetDatum(created_at_tz);

	values[5] = Int64GetDatum((int64) hdr->start_lsn);
	values[6] = Int64GetDatum((int64) hdr->stop_lsn);
	values[7] = Int32GetDatum((int32) hdr->pg_version);
	values[8] = BoolGetDatum(hdr->compressed);
	values[9] = BoolGetDatum(hdr->encrypted);
	values[10] = Int64GetDatum((int64) hdr->base_backup_lsn);

	tuple = heap_form_tuple(tupdesc, values, nulls);

	bakfile_close_reader(reader);

	return HeapTupleGetDatum(tuple);
}

Datum
inspect_filelist(FunctionCallInfo fcinfo)
{
	text	   *filepath_text;
	char	   *filepath;
	char	   *password = NULL;
	ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
	TupleDesc	tupdesc;
	Tuplestorestate *tupstore;
	MemoryContext per_query_ctx;
	MemoryContext oldcontext;
	BakFileReader *reader;
	uint8		section_type;
	bool		data_found = false;

	if (PG_ARGISNULL(0))
		ereport(ERROR,
				(errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
				 errmsg("filepath cannot be NULL")));

	filepath_text = PG_GETARG_TEXT_PP(0);
	filepath = text_to_cstring(filepath_text);

	if (PG_NARGS() > 1 && !PG_ARGISNULL(1))
		password = text_to_cstring(PG_GETARG_TEXT_PP(1));

	if (rsinfo == NULL || !IsA(rsinfo, ReturnSetInfo) ||
		(rsinfo->allowedModes & SFRM_Materialize) == 0)
		ereport(ERROR,
				(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
				 errmsg("set-valued function called in context that cannot accept a set")));

	per_query_ctx = rsinfo->econtext->ecxt_per_query_memory;
	oldcontext = MemoryContextSwitchTo(per_query_ctx);

	if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE)
		ereport(ERROR,
				(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
				 errmsg("function returning record called in context that cannot accept type record")));

	tupdesc = CreateTupleDescCopy(tupdesc);
	tupstore = tuplestore_begin_heap(true, false, work_mem);
	rsinfo->returnMode = SFRM_Materialize;
	rsinfo->setResult = tupstore;
	rsinfo->setDesc = tupdesc;

	MemoryContextSwitchTo(oldcontext);

	reader = bakfile_open(filepath, password);

	while ((section_type = bakfile_next_section(reader)) != 0)
	{
		if (section_type == BAKSECTION_DATA)
		{
			uint32		count;
			BakDataEntryMeta *metas;
			uint32		i;

			metas = bakfile_list_data_entries(reader, &count);

			for (i = 0; i < count; i++)
			{
				Datum		values[3];
				bool		nulls[3] = {false, false, false};
				char	   *hex;

				values[0] = CStringGetTextDatum(metas[i].path);
				values[1] = Int64GetDatum((int64) metas[i].data_len);
				hex = hex_encode_digest(metas[i].digest, 32);
				values[2] = CStringGetTextDatum(hex);

				tuplestore_putvalues(tupstore, tupdesc, values, nulls);
			}

			data_found = true;
			break;
		}

		if (reader->section_plain_len > 0
			&& reader->section_plain_pos < reader->section_plain_len)
		{
			/* Skip rest of section; next call re-reads. */
		}
	}

	bakfile_close_reader(reader);

	(void) data_found;
	return (Datum) 0;
}

Datum
inspect_verify(FunctionCallInfo fcinfo)
{
	text	   *filepath_text = PG_GETARG_TEXT_PP(0);
	char	   *filepath = text_to_cstring(filepath_text);
	char	   *detail = NULL;
	bool		ok;
	TupleDesc	tupdesc;
	Datum		values[2];
	bool		nulls[2] = {false, false};
	HeapTuple	tuple;

	ok = bakfile_verify(filepath, NULL, &detail);

	if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE)
		ereport(ERROR,
				(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
				 errmsg("function returning record called in context that cannot accept type record")));

	tupdesc = BlessTupleDesc(tupdesc);

	values[0] = BoolGetDatum(ok);
	if (detail != NULL)
		values[1] = CStringGetTextDatum(detail);
	else
		nulls[1] = true;

	tuple = heap_form_tuple(tupdesc, values, nulls);
	return HeapTupleGetDatum(tuple);
}
