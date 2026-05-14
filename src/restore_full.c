#include "postgres.h"

#include <libpq-fe.h>

#include "common/cryptohash.h"
#include "lib/stringinfo.h"
#include "miscadmin.h"
#include "port.h"
#include "port/pg_bswap.h"
#include "storage/latch.h"
#include "utils/builtins.h"
#include "utils/wait_event.h"

#include "bakfile.h"
#include "libpq_helpers.h"
#include "restore_full.h"

#include "dbbackup_defaults.h"

static void
logical_libpq_copy_in_from_reader(PGconn *conn, BakFileReader *reader,
								  BakDataEntry *entry)
{
	StringInfoData copy_sql;
	PGresult   *res;
	ExecStatusType st;
	const char *dot;
	char	   *schema;
	char	   *relname;
	char	   *cols;
	char	   *buf;
	uint64		remaining;
	pg_cryptohash_ctx *ctx;
	uint8		computed[32];
	int			r;

	dot = strchr(entry->path, '.');
	if (dot == NULL)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("data entry path missing schema separator: \"%s\"",
						entry->path)));
	schema = pnstrdup(entry->path, dot - entry->path);
	relname = pstrdup(dot + 1);
	cols = pgbu_libpq_copy_column_list(conn, schema, relname, entry->path);

	initStringInfo(&copy_sql);
	appendStringInfo(&copy_sql,
					 "COPY %s.%s (%s) FROM STDIN (FORMAT binary)",
					 pgbu_quote_db_identifier(schema),
					 pgbu_quote_db_identifier(relname),
					 cols);
	pfree(cols);

	res = PQexec(conn, copy_sql.data);
	st = PQresultStatus(res);
	if (st != PGRES_COPY_IN)
	{
		char	   *msg = pstrdup(PQerrorMessage(conn));

		PQclear(res);
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("COPY FROM STDIN setup failed for \"%s\"", entry->path),
				 errdetail("%s", msg)));
	}
	PQclear(res);

	ctx = pg_cryptohash_create(PG_SHA256);
	if (ctx == NULL || pg_cryptohash_init(ctx) < 0)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not initialize SHA-256 context")));

	buf = palloc(PGDBBACKUP_COPY_CHUNK_SIZE);
	remaining = entry->data_len;
	while (remaining > 0)
	{
		size_t		chunk = (remaining > PGDBBACKUP_COPY_CHUNK_SIZE) ?
			PGDBBACKUP_COPY_CHUNK_SIZE : (size_t) remaining;

		CHECK_FOR_INTERRUPTS();
		bakfile_read_data_entry_chunk(reader, buf, chunk);
		if (pg_cryptohash_update(ctx, buf, chunk) < 0)
			ereport(ERROR,
					(errcode(ERRCODE_INTERNAL_ERROR),
					 errmsg("could not update SHA-256 context")));

		r = PQputCopyData(conn, buf, (int) chunk);
		if (r != 1)
			ereport(ERROR,
					(errcode(ERRCODE_INTERNAL_ERROR),
					 errmsg("PQputCopyData failed for \"%s\": %s",
							entry->path, PQerrorMessage(conn))));
		remaining -= chunk;
	}
	pfree(buf);

	if (pg_cryptohash_final(ctx, computed, sizeof(computed)) < 0)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not finalize SHA-256 context")));
	pg_cryptohash_free(ctx);

	r = PQputCopyEnd(conn, NULL);
	if (r != 1)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("PQputCopyEnd failed for \"%s\": %s",
						entry->path, PQerrorMessage(conn))));

	for (;;)
	{
		while (PQisBusy(conn))
		{
			int			sock = PQsocket(conn);
			WaitEvent	evt;
			WaitEventSet *wes;

			CHECK_FOR_INTERRUPTS();

			if (sock < 0)
				break;

			wes = CreateWaitEventSet(NULL, 2);
			AddWaitEventToSet(wes, WL_SOCKET_READABLE, sock, NULL, NULL);
			AddWaitEventToSet(wes, WL_EXIT_ON_PM_DEATH, PGINVALID_SOCKET,
							  NULL, NULL);
			(void) WaitEventSetWait(wes, 1000L, &evt, 1, PG_WAIT_EXTENSION);
			FreeWaitEventSet(wes);

			if (PQconsumeInput(conn) == 0)
				break;
		}

		res = PQgetResult(conn);
		if (res == NULL)
			break;

		st = PQresultStatus(res);
		if (st != PGRES_COMMAND_OK)
		{
			char	   *msg = pstrdup(PQerrorMessage(conn));

			PQclear(res);
			ereport(ERROR,
					(errcode(ERRCODE_INTERNAL_ERROR),
					 errmsg("COPY FROM failed for \"%s\"", entry->path),
					 errdetail("%s", msg)));
		}
		PQclear(res);
	}

	bakfile_finish_data_entry(reader, entry, NULL, false);
	if (memcmp(entry->checksum, computed, sizeof(computed)) != 0)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("data entry SHA-256 mismatch for \"%s\"",
						entry->path)));

	pfree(copy_sql.data);
	pfree(schema);
	pfree(relname);
}

static void
restore_logical_full_data_entries(PGconn *conn, const char *filepath,
								  const char *password,
								  uint32 expected_entry_count)
{
	BakFileReader *reader;
	uint8		stype;
	uint32		entry_count;

	reader = bakfile_open(filepath, password);

	stype = bakfile_next_section(reader);
	if (stype != BAKSECTION_METADATA)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("expected METADATA section, got %u", stype)));

	stype = bakfile_next_section(reader);
	if (stype != BAKSECTION_SCHEMA)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("expected SCHEMA section, got %u", stype)));

	stype = bakfile_next_section(reader);
	if (stype != BAKSECTION_DATA)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("expected DATA section, got %u", stype)));

	entry_count = bakfile_read_data_entry_count(reader);
	if (entry_count != expected_entry_count)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("DATA entry count changed while restoring \"%s\"",
						filepath)));

	for (uint32 k = 0; k < entry_count; k++)
	{
		BakDataEntry entry;

		CHECK_FOR_INTERRUPTS();
		if (!bakfile_next_data_entry(reader, &entry))
			ereport(ERROR,
					(errcode(ERRCODE_DATA_CORRUPTED),
					 errmsg("DATA section ended early at entry %u of %u",
							k, entry_count)));

		ereport(NOTICE,
				(errmsg("restoring table %u/%u: %s",
						k + 1, entry_count, entry.path)));
		logical_libpq_copy_in_from_reader(conn, reader, &entry);
		pfree(entry.path);
	}

	bakfile_close_reader(reader);
}

typedef struct ParsedLogicalFull
{
	char	   *db_name;
	Oid			db_oid;
	uint64		stop_lsn;
	int64		created_at;
	char	   *metadata_sql;
	char	   *schema_sql;
	uint32		entry_count;
} ParsedLogicalFull;

static bool
logical_full_backup_has_schema(const char *filepath, const char *password)
{
	BakFileReader *reader;
	uint8		stype;
	bool		has_schema;

	reader = bakfile_open(filepath, password);
	if (reader->header.mode != BACKUP_MODE_FULL ||
		reader->header.type != BACKUP_TYPE_FULL)
	{
		bakfile_close_reader(reader);
		return false;
	}

	stype = bakfile_next_section(reader);
	if (stype != BAKSECTION_METADATA)
	{
		bakfile_close_reader(reader);
		return false;
	}

	stype = bakfile_next_section(reader);
	has_schema = (stype == BAKSECTION_SCHEMA);
	bakfile_close_reader(reader);
	return has_schema;
}

static void
load_logical_full_backup(const char *filepath, const char *password,
						 ParsedLogicalFull *out)
{
	BakFileReader *reader;
	uint8		stype;
	char	   *buf;
	size_t		buf_len;

	memset(out, 0, sizeof(*out));

	reader = bakfile_open(filepath, password);
	if (reader->header.mode != BACKUP_MODE_FULL ||
		reader->header.type != BACKUP_TYPE_FULL)
	{
		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("\"%s\" is not a FULL logical base backup", filepath)));
	}

	out->db_name = pstrdup(reader->header.db_name);
	out->db_oid = reader->header.db_oid;
	out->stop_lsn = reader->header.stop_lsn;
	out->created_at = reader->header.created_at;

	stype = bakfile_next_section(reader);
	if (stype != BAKSECTION_METADATA)
	{
		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("expected METADATA section, got %u", stype)));
	}
	bakfile_read_section_all(reader, &buf, &buf_len);
	out->metadata_sql = buf;

	stype = bakfile_next_section(reader);
	if (stype != BAKSECTION_SCHEMA)
	{
		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("expected SCHEMA section, got %u", stype)));
	}
	bakfile_read_section_all(reader, &buf, &buf_len);
	out->schema_sql = buf;

	stype = bakfile_next_section(reader);
	if (stype != BAKSECTION_DATA)
	{
		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("expected DATA section, got %u", stype)));
	}

	out->entry_count = bakfile_read_data_entry_count(reader);

	bakfile_close_reader(reader);
}

static uint32
logical_read_u32(const char *buf, size_t buf_len, size_t *pos,
				 const char *filepath)
{
	uint32		net;

	if (*pos + sizeof(net) > buf_len)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("truncated logical PITR stream in \"%s\"", filepath)));

	memcpy(&net, buf + *pos, sizeof(net));
	*pos += sizeof(net);
	return pg_ntoh32(net);
}

static bool
logical_apply_log_file(PGconn *conn, const char *filepath,
					   const char *password, const char *expected_db_name,
					   uint64 expected_prev_stop_lsn,
					   TimestampTz stop_at, bool has_stop_at,
					   uint64 *stop_lsn_out)
{
	BakFileReader *reader;
	uint8		stype;
	char	   *metadata_sql = NULL;
	char	   *buf;
	size_t		metadata_len = 0;
	size_t		buf_len;
	size_t		pos;
	uint32		frame_count;
	StringInfoData txn_sql;
	bool		in_txn = false;
	bool		stopped = false;

	reader = bakfile_open(filepath, password);
	if (reader->header.mode != BACKUP_MODE_FULL ||
		(reader->header.type != BACKUP_TYPE_LOG &&
		 reader->header.type != BACKUP_TYPE_DIFFERENTIAL))
	{
		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("\"%s\" is not a FULL logical stream backup", filepath)));
	}
	if (strcmp(reader->header.db_name, expected_db_name) != 0)
	{
		char	   *got = pstrdup(reader->header.db_name);

		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("chain entry is for db \"%s\", not \"%s\"",
						got, expected_db_name)));
	}
	if (reader->header.base_backup_lsn != expected_prev_stop_lsn)
	{
		uint64		got = reader->header.base_backup_lsn;

		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("chain LSN gap in \"%s\"", filepath),
				 errdetail("Expected base_backup_lsn " UINT64_FORMAT
						   ", got " UINT64_FORMAT ".",
						   expected_prev_stop_lsn, got)));
	}
	*stop_lsn_out = reader->header.stop_lsn;

	stype = bakfile_next_section(reader);
	if (stype != BAKSECTION_METADATA)
	{
		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("expected METADATA section, got %u", stype)));
	}
	bakfile_read_section_all(reader, &metadata_sql, &metadata_len);

	stype = bakfile_next_section(reader);
	if (stype != BAKSECTION_LOGICAL_STREAM)
	{
		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("expected LOGICAL_STREAM section, got %u", stype)));
	}
	bakfile_read_section_all(reader, &buf, &buf_len);
	bakfile_close_reader(reader);

	pos = 0;
	frame_count = logical_read_u32(buf, buf_len, &pos, filepath);
	initStringInfo(&txn_sql);

	for (uint32 i = 0; i < frame_count; i++)
	{
		uint32		len;
		char	   *frame;

		CHECK_FOR_INTERRUPTS();

		len = logical_read_u32(buf, buf_len, &pos, filepath);
		if (pos + len > buf_len)
			ereport(ERROR,
					(errcode(ERRCODE_DATA_CORRUPTED),
					 errmsg("truncated logical PITR frame in \"%s\"", filepath)));

		frame = pnstrdup(buf + pos, len);
		pos += len;

		if (strncmp(frame, "BEGIN\t", 6) == 0)
		{
			resetStringInfo(&txn_sql);
			in_txn = true;
		}
		else if (strncmp(frame, "SQL\t", 4) == 0)
		{
			if (!in_txn)
				ereport(ERROR,
						(errcode(ERRCODE_DATA_CORRUPTED),
						 errmsg("logical SQL frame appeared outside a transaction in \"%s\"",
								filepath)));
			appendStringInfoString(&txn_sql, frame + 4);
			appendStringInfoChar(&txn_sql, '\n');
		}
		else if (strcmp(frame, "NOOP") == 0)
		{
			/* Logical slot keepalive used to make failover slots sync-ready. */
		}
		else if (strncmp(frame, "COMMIT\t", 7) == 0)
		{
			char	   *ts = frame + 7;
			char	   *tab = strchr(ts, '\t');
			TimestampTz commit_time;

			if (tab == NULL)
				ereport(ERROR,
						(errcode(ERRCODE_DATA_CORRUPTED),
						 errmsg("malformed COMMIT frame in \"%s\"", filepath)));
			*tab = '\0';
			commit_time = (TimestampTz) strtoll(ts, NULL, 10);

			if (has_stop_at && commit_time > stop_at)
			{
				stopped = true;
				pfree(frame);
				break;
			}

			if (in_txn && txn_sql.len > 0)
			{
				StringInfoData replay_sql;

				initStringInfo(&replay_sql);
				appendStringInfoString(&replay_sql, "BEGIN;\n");
				appendStringInfoString(&replay_sql,
									   "SET LOCAL session_replication_role = replica;\n"
									   "SET LOCAL dbbackup.replaying = 'on';\n");
				appendBinaryStringInfo(&replay_sql, txn_sql.data,
									   txn_sql.len);
				appendStringInfoString(&replay_sql, "COMMIT;\n");
				pgbu_libpq_exec(conn, replay_sql.data,
								"logical transaction replay");
				pfree(replay_sql.data);
			}
			resetStringInfo(&txn_sql);
			in_txn = false;
		}
		else
		{
			ereport(ERROR,
					(errcode(ERRCODE_DATA_CORRUPTED),
					 errmsg("unknown logical PITR frame in \"%s\"", filepath)));
		}

		pfree(frame);
	}

	if (!stopped && metadata_len > 0)
		pgbu_libpq_exec(conn, metadata_sql, "logical stream METADATA");

	pfree(txn_sql.data);
	pfree(metadata_sql);
	pfree(buf);
	return stopped;
}

static void
validate_logical_restore_chain(char **files, int file_count,
							   const char *password,
							   const char *expected_db_name,
							   uint64 full_stop_lsn)
{
	uint64		prev_stop_lsn = full_stop_lsn;
	int			diff_count = 0;

	for (int i = 1; i < file_count; i++)
	{
		BakFileReader *reader = bakfile_open(files[i], password);
		PgDbBackupType type = reader->header.type;
		uint64		base_backup_lsn = reader->header.base_backup_lsn;
		uint64		stop_lsn = reader->header.stop_lsn;

		if (reader->header.mode != BACKUP_MODE_FULL)
		{
			bakfile_close_reader(reader);
			ereport(ERROR,
					(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
					 errmsg("chain entry %d is not FULL mode", i)));
		}

		if (strcmp(reader->header.db_name, expected_db_name) != 0)
		{
			char	   *got = pstrdup(reader->header.db_name);

			bakfile_close_reader(reader);
			ereport(ERROR,
					(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
					 errmsg("chain entry %d is for db \"%s\", not \"%s\"",
							i, got, expected_db_name)));
		}

		if (type == BACKUP_TYPE_DIFFERENTIAL)
		{
			diff_count++;
			if (diff_count > 1)
			{
				bakfile_close_reader(reader);
				ereport(ERROR,
						(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
						 errmsg("chain may contain at most one DIFFERENTIAL .bak")));
			}
			if (i != 1)
			{
				bakfile_close_reader(reader);
				ereport(ERROR,
						(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
						 errmsg("DIFFERENTIAL must immediately follow the FULL .bak in the chain")));
			}
		}
		else if (type != BACKUP_TYPE_LOG)
		{
			bakfile_close_reader(reader);
			ereport(ERROR,
					(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
					 errmsg("chain entry %d has unexpected type for follow-up backup",
							i)));
		}

		if (base_backup_lsn != prev_stop_lsn)
		{
			bakfile_close_reader(reader);
			ereport(ERROR,
					(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
					 errmsg("chain LSN gap at file %d", i),
					 errdetail("Expected base_backup_lsn " UINT64_FORMAT
							   ", got " UINT64_FORMAT ".",
							   prev_stop_lsn, base_backup_lsn)));
		}

		prev_stop_lsn = stop_lsn;
		bakfile_close_reader(reader);
	}
}

static void
repair_owned_sequence_state(PGconn *conn)
{
	const char *sql =
		"DO $pg_dbbackup_seq$\n"
		"DECLARE\n"
		"  r record;\n"
		"  max_value numeric;\n"
		"  last_value numeric;\n"
		"  qualified text;\n"
		"BEGIN\n"
		"  FOR r IN\n"
		"    SELECT seq_ns.nspname AS seq_schema,\n"
		"           seq.relname AS seq_name,\n"
		"           tbl_ns.nspname AS table_schema,\n"
		"           tbl.relname AS table_name,\n"
		"           att.attname AS column_name\n"
		"    FROM pg_class seq\n"
		"    JOIN pg_namespace seq_ns ON seq_ns.oid = seq.relnamespace\n"
		"    JOIN pg_depend dep ON dep.objid = seq.oid\n"
		"    JOIN pg_class tbl ON tbl.oid = dep.refobjid\n"
		"    JOIN pg_namespace tbl_ns ON tbl_ns.oid = tbl.relnamespace\n"
		"    JOIN pg_attribute att ON att.attrelid = tbl.oid\n"
		"                         AND att.attnum = dep.refobjsubid\n"
		"    WHERE seq.relkind = 'S'\n"
		"      AND dep.deptype IN ('a', 'i')\n"
		"      AND tbl_ns.nspname NOT LIKE 'pg\\_%'\n"
		"      AND tbl_ns.nspname NOT IN ('information_schema', 'dbbackup')\n"
		"  LOOP\n"
		"    EXECUTE format('SELECT max(%I)::numeric FROM %I.%I',\n"
		"                   r.column_name, r.table_schema, r.table_name)\n"
		"      INTO max_value;\n"
		"    IF max_value IS NULL THEN\n"
		"      CONTINUE;\n"
		"    END IF;\n"
		"    EXECUTE format('SELECT last_value::numeric FROM %I.%I',\n"
		"                   r.seq_schema, r.seq_name)\n"
		"      INTO last_value;\n"
		"    IF last_value IS NULL OR max_value > last_value THEN\n"
		"      qualified := format('%I.%I', r.seq_schema, r.seq_name);\n"
		"      EXECUTE format('SELECT pg_catalog.setval(%L::regclass, %s, true)',\n"
		"                     qualified, max_value::bigint);\n"
		"    END IF;\n"
		"  END LOOP;\n"
		"END\n"
		"$pg_dbbackup_seq$;";

	pgbu_libpq_exec(conn, sql, "repair owned sequence state");
}

static void
prepare_logical_continuation(const char *target_db, const char *source_db,
							 uint64 confirmed_lsn)
{
	PGconn	   *conn;
	char		lsn_buf[64];
	char	   *mode_sql;

	snprintf(lsn_buf, sizeof(lsn_buf), "%X/%X",
			 (uint32) (confirmed_lsn >> 32), (uint32) confirmed_lsn);

	conn = pgbu_connect_libpq(target_db);
	PG_TRY();
	{
		pgbu_libpq_exec(conn, "CREATE EXTENSION IF NOT EXISTS pg_dbbackup",
						"CREATE EXTENSION pg_dbbackup");

		mode_sql = psprintf(
			"SELECT dbbackup.pg_dbbackup_set_mode(%s, 'full')",
			quote_literal_cstr(target_db));
		pgbu_libpq_exec(conn, mode_sql, "set restored DB backup mode");
		pfree(mode_sql);

		if (strcmp(target_db, source_db) == 0)
		{
			PGresult   *res;
			char	   *oid_text;
			char	   *slot_name;
			char	   *drop_sql;
			char	   *create_sql;
			char	   *chain_sql;

			res = PQexec(conn,
						 "SELECT oid::text FROM pg_database "
						 "WHERE datname = current_database()");
			if (res == NULL || PQresultStatus(res) != PGRES_TUPLES_OK ||
				PQntuples(res) != 1)
			{
				char	   *msg = res ? pstrdup(PQerrorMessage(conn)) :
					pstrdup("no result");

				if (res)
					PQclear(res);
				ereport(ERROR,
						(errcode(ERRCODE_INTERNAL_ERROR),
						 errmsg("could not resolve restored database oid"),
						 errdetail("%s", msg)));
			}

			oid_text = pstrdup(PQgetvalue(res, 0, 0));
			PQclear(res);

			slot_name = psprintf("_pg_dbbackup_%s", oid_text);
			drop_sql = psprintf(
				"SELECT pg_drop_replication_slot(%s) "
				"WHERE EXISTS (SELECT 1 FROM pg_replication_slots "
				"              WHERE slot_name = %s)",
				quote_literal_cstr(slot_name),
				quote_literal_cstr(slot_name));
			create_sql = psprintf(
				"SELECT pg_create_logical_replication_slot(%s, 'pg_dbbackup', false, false, true)",
				quote_literal_cstr(slot_name));
			chain_sql = psprintf(
				"INSERT INTO dbbackup.logical_chains "
				"(db_oid, db_name, slot_name, confirmed_lsn, updated_at) "
				"VALUES ((SELECT oid FROM pg_database "
				"         WHERE datname = current_database()), "
				"        current_database(), %s, %s::pg_lsn, clock_timestamp()) "
				"ON CONFLICT (db_oid) DO UPDATE SET "
				"db_name = EXCLUDED.db_name, "
				"slot_name = EXCLUDED.slot_name, "
				"confirmed_lsn = EXCLUDED.confirmed_lsn, "
				"updated_at = EXCLUDED.updated_at",
				quote_literal_cstr(slot_name),
				quote_literal_cstr(lsn_buf));

			pgbu_libpq_exec(conn, drop_sql, "drop stale continuation slot");
			pgbu_libpq_exec(conn, create_sql, "create continuation slot");
			pgbu_libpq_exec(conn, chain_sql, "record continuation chain");

			pfree(chain_sql);
			pfree(create_sql);
			pfree(drop_sql);
			pfree(slot_name);
			pfree(oid_text);
		}
	}
	PG_CATCH();
	{
		PQfinish(conn);
		PG_RE_THROW();
	}
	PG_END_TRY();
	PQfinish(conn);
}

static void
restore_full_logical(const char *target_db, char **files, int file_count,
					 TimestampTz stop_at, bool has_stop_at,
					 const char *password)
{
	ParsedLogicalFull full;
	char	   *temp_dbname;
	uint64		prev_stop_lsn;
	int			i;

	load_logical_full_backup(files[0], password, &full);
	prev_stop_lsn = full.stop_lsn;
	validate_logical_restore_chain(files, file_count, password,
								   full.db_name, full.stop_lsn);

	temp_dbname = pgbu_generate_temp_dbname();

	ereport(NOTICE,
			(errmsg("creating temp DB \"%s\"", temp_dbname)));

	{
		PGconn	   *admin = pgbu_connect_libpq("postgres");
		char	   *create_sql;

		PG_TRY();
		{
			create_sql = psprintf(
				"CREATE DATABASE %s TEMPLATE template0",
				pgbu_quote_db_identifier(temp_dbname));
			pgbu_libpq_exec(admin, create_sql, "CREATE DATABASE temp");
			pfree(create_sql);
		}
		PG_CATCH();
		{
			PQfinish(admin);
			PG_RE_THROW();
		}
		PG_END_TRY();
		PQfinish(admin);
	}

	PG_TRY();
	{
		PGconn	   *conn = pgbu_connect_libpq(temp_dbname);

		PG_TRY();
		{
			if (full.metadata_sql && full.metadata_sql[0])
				pgbu_libpq_exec_schema_idempotent(conn, full.metadata_sql);

			if (full.schema_sql && full.schema_sql[0])
			{
				ereport(NOTICE,
						(errmsg("applying SCHEMA section")));
				pgbu_libpq_exec(conn, full.schema_sql, "SCHEMA");
			}

			pgbu_libpq_exec(conn, "SET session_replication_role = replica",
							"disable restore triggers for FULL data load");
			restore_logical_full_data_entries(conn, files[0], password,
											  full.entry_count);
			pgbu_libpq_exec(conn, "SET session_replication_role = origin",
							"restore trigger firing mode after FULL data load");

			if (full.metadata_sql && full.metadata_sql[0])
			{
				ereport(NOTICE,
						(errmsg("applying METADATA")));
				pgbu_libpq_exec(conn, full.metadata_sql, "METADATA");
			}

			if (has_stop_at && stop_at < (TimestampTz) full.created_at)
			{
				ereport(NOTICE,
						(errmsg("stop_at point is before the FULL backup window; using captured FULL image")));
			}
			else
			{
				for (i = 1; i < file_count; i++)
				{
					bool		stopped;
					uint64		next_stop_lsn;

					ereport(NOTICE,
							(errmsg("applying logical stream backup %d/%d",
									i, file_count - 1)));
					stopped = logical_apply_log_file(conn, files[i], password,
													 full.db_name,
													 prev_stop_lsn,
													 stop_at, has_stop_at,
													 &next_stop_lsn);
					prev_stop_lsn = next_stop_lsn;
					if (stopped)
						break;
				}
			}

			repair_owned_sequence_state(conn);
		}
		PG_CATCH();
		{
			PQfinish(conn);
			PG_RE_THROW();
		}
		PG_END_TRY();

		PQfinish(conn);
	}
	PG_CATCH();
	{
		pgbu_drop_temp_db_quiet(temp_dbname);
		PG_RE_THROW();
	}
	PG_END_TRY();

	{
		PGconn	   *admin = pgbu_connect_libpq("postgres");
		char	   *drop_sql;
		char	   *rename_sql;

		PG_TRY();
		{
			drop_sql = psprintf(
				"DROP DATABASE IF EXISTS %s WITH (FORCE)",
				pgbu_quote_db_identifier(target_db));
			rename_sql = psprintf(
				"ALTER DATABASE %s RENAME TO %s",
				pgbu_quote_db_identifier(temp_dbname),
				pgbu_quote_db_identifier(target_db));

			pgbu_libpq_exec(admin, drop_sql, "DROP DATABASE target");

			ereport(NOTICE,
					(errmsg("renaming %s -> %s", temp_dbname, target_db)));

			pgbu_libpq_exec(admin, rename_sql, "ALTER DATABASE RENAME");

			pfree(drop_sql);
			pfree(rename_sql);
		}
		PG_CATCH();
		{
			PQfinish(admin);
			pgbu_drop_temp_db_quiet(temp_dbname);
			PG_RE_THROW();
		}
		PG_END_TRY();
		PQfinish(admin);
	}

	prepare_logical_continuation(target_db, full.db_name, prev_stop_lsn);

	pfree(temp_dbname);
}


void
restore_full(const char *target_db, char **files, int file_count,
			 TimestampTz stop_at, bool has_stop_at, const char *password)
{
	if (!superuser())
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
				 errmsg("must be superuser to perform restores")));

	if (file_count < 1)
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("at least one .bak file required")));

	if (logical_full_backup_has_schema(files[0], password))
	{
		restore_full_logical(target_db, files, file_count,
							 stop_at, has_stop_at, password);
		return;
	}

	ereport(ERROR,
			(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
			 errmsg("FULL restore requires a v1 logical FULL backup"),
			 errdetail("The first .bak has no SCHEMA section and looks like an obsolete physical FULL artifact.")));

}
