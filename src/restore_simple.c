#include "postgres.h"

#include <libpq-fe.h>
#include <unistd.h>

#include "lib/stringinfo.h"
#include "miscadmin.h"
#include "port.h"
#include "storage/latch.h"
#include "utils/builtins.h"
#include "utils/wait_event.h"

#include "bakfile.h"
#include "libpq_helpers.h"
#include "restore_simple.h"

static void
libpq_copy_in(PGconn *conn, const char *path, const char *data, size_t data_len)
{
	StringInfoData copy_sql;
	PGresult   *res;
	ExecStatusType st;
	const char *dot;
	char	   *schema;
	char	   *relname;
	int			r;

	dot = strchr(path, '.');
	if (dot == NULL)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("data entry path missing schema separator: \"%s\"",
						path)));
	schema = pnstrdup(path, dot - path);
	relname = pstrdup(dot + 1);

	initStringInfo(&copy_sql);
	appendStringInfo(&copy_sql,
					 "COPY %s.%s FROM STDIN (FORMAT binary)",
					 pgbu_quote_db_identifier(schema),
					 pgbu_quote_db_identifier(relname));

	res = PQexec(conn, copy_sql.data);
	st = PQresultStatus(res);
	if (st != PGRES_COPY_IN)
	{
		char	   *msg = pstrdup(PQerrorMessage(conn));

		PQclear(res);
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("COPY FROM STDIN setup failed for \"%s\"", path),
				 errdetail("%s", msg)));
	}
	PQclear(res);

	if (data_len > 0)
	{
		r = PQputCopyData(conn, data, (int) data_len);
		if (r != 1)
			ereport(ERROR,
					(errcode(ERRCODE_INTERNAL_ERROR),
					 errmsg("PQputCopyData failed for \"%s\": %s",
							path, PQerrorMessage(conn))));
	}

	r = PQputCopyEnd(conn, NULL);
	if (r != 1)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("PQputCopyEnd failed for \"%s\": %s",
						path, PQerrorMessage(conn))));

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
					 errmsg("COPY FROM failed for \"%s\"", path),
					 errdetail("%s", msg)));
		}
		PQclear(res);
	}

	pfree(copy_sql.data);
	pfree(schema);
	pfree(relname);
}

typedef struct ParsedSimpleBackup
{
	char	   *metadata_sql;
	char	   *schema_sql;
	uint32		entry_count;
	BakDataEntry *entries;
	char	  **entry_data;
	char	   *db_name;
	PgDbBackupType type;
} ParsedSimpleBackup;

static void
load_simple_backup(const char *filepath, const char *password,
				   ParsedSimpleBackup *out)
{
	BakFileReader *reader;
	uint8		stype;
	char	   *buf;
	size_t		buf_len;
	uint32		i;

	memset(out, 0, sizeof(*out));

	reader = bakfile_open(filepath, password);

	if (reader->header.mode != BACKUP_MODE_SIMPLE)
	{
		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("\"%s\" is not a SIMPLE-mode backup", filepath)));
	}
	if (reader->header.type != BACKUP_TYPE_FULL &&
		reader->header.type != BACKUP_TYPE_DIFFERENTIAL)
	{
		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
				 errmsg("\"%s\" has unsupported backup type for SIMPLE restore",
						filepath)));
	}

	out->type = reader->header.type;
	out->db_name = pstrdup(reader->header.db_name);

	/* METADATA */
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

	/* SCHEMA */
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

	/* DATA */
	stype = bakfile_next_section(reader);
	if (stype != BAKSECTION_DATA)
	{
		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("expected DATA section, got %u", stype)));
	}

	out->entry_count = bakfile_read_data_entry_count(reader);

	if (out->entry_count > 0)
	{
		out->entries = palloc0(sizeof(BakDataEntry) * out->entry_count);
		out->entry_data = palloc0(sizeof(char *) * out->entry_count);
	}

	for (i = 0; i < out->entry_count; i++)
	{
		BakDataEntry *e = &out->entries[i];
		char	   *data;

		if (!bakfile_next_data_entry(reader, e))
			ereport(ERROR,
					(errcode(ERRCODE_DATA_CORRUPTED),
					 errmsg("DATA section ended early at entry %u of %u",
							i, out->entry_count)));

		data = palloc(e->data_len == 0 ? 1 : (size_t) e->data_len);
		if (e->data_len > 0)
			bakfile_read_data_entry_chunk(reader, data, (size_t) e->data_len);

		bakfile_finish_data_entry(reader, e, data, true);
		out->entry_data[i] = data;
	}

	bakfile_close_reader(reader);
}

static void
libpq_truncate(PGconn *conn, const char *path)
{
	const char *dot = strchr(path, '.');
	char	   *schema;
	char	   *relname;
	StringInfoData sql;

	if (dot == NULL)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("data entry path missing schema separator: \"%s\"",
						path)));
	schema = pnstrdup(path, dot - path);
	relname = pstrdup(dot + 1);

	initStringInfo(&sql);
	appendStringInfo(&sql, "TRUNCATE TABLE %s.%s",
					 pgbu_quote_db_identifier(schema),
					 pgbu_quote_db_identifier(relname));
	pgbu_libpq_exec(conn, sql.data, "TRUNCATE diff table");

	pfree(sql.data);
	pfree(schema);
	pfree(relname);
}

void
restore_simple(const char *target_db, char **files, int file_count,
			   const char *password)
{
	char	   *temp_dbname;
	ParsedSimpleBackup *parsed_list;
	const char *first_db_name;
	int			i;
	int			diff_count = 0;

	if (!superuser())
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
				 errmsg("must be superuser to perform restores")));

	if (file_count < 1)
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("at least one .bak file required")));

	parsed_list = palloc0(sizeof(ParsedSimpleBackup) * file_count);

	load_simple_backup(files[0], password, &parsed_list[0]);
	if (parsed_list[0].type != BACKUP_TYPE_FULL)
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("first .bak in restore chain must be a SIMPLE FULL backup")));
	first_db_name = parsed_list[0].db_name;

	for (i = 1; i < file_count; i++)
	{
		load_simple_backup(files[i], password, &parsed_list[i]);
		if (strcmp(parsed_list[i].db_name, first_db_name) != 0)
			ereport(ERROR,
					(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
					 errmsg("mismatched db_name across .bak chain: \"%s\" vs \"%s\"",
							parsed_list[i].db_name, first_db_name)));
		if (parsed_list[i].type != BACKUP_TYPE_DIFFERENTIAL)
			ereport(ERROR,
					(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
					 errmsg("only one SIMPLE DIFFERENTIAL backup may follow the FULL in a chain")));
		diff_count++;
	}

	if (diff_count > 1)
		ereport(ERROR,
				(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
				 errmsg("SIMPLE restore supports at most one DIFFERENTIAL backup"),
				 errhint("DIFFERENTIAL backups are cumulative; pass only the latest one.")));

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
		uint32		k;
		int			f;

		PG_TRY();
		{
			ParsedSimpleBackup *full = &parsed_list[0];

			/*
			 * METADATA carries CREATE EXTENSION / CREATE SCHEMA statements
			 * that SCHEMA's DDL depends on (e.g. a column of type vector(3)
			 * needs the vector extension). Apply metadata idempotently first
			 * so extensions and schemas exist before SCHEMA runs. GRANT and
			 * COMMENT statements targeting tables that do not yet exist will
			 * fail individually and be swallowed by the idempotent wrapper;
			 * they are reapplied normally after DATA below.
			 */
			if (full->metadata_sql && full->metadata_sql[0])
				pgbu_libpq_exec_schema_idempotent(conn, full->metadata_sql);

			if (full->schema_sql && full->schema_sql[0])
			{
				ereport(NOTICE,
						(errmsg("applying SCHEMA section")));
				pgbu_libpq_exec(conn, full->schema_sql, "SCHEMA");
			}

			for (k = 0; k < full->entry_count; k++)
			{
				BakDataEntry *e = &full->entries[k];

				CHECK_FOR_INTERRUPTS();
				ereport(NOTICE,
						(errmsg("restoring table %u/%u: %s",
								k + 1, full->entry_count, e->path)));
				libpq_copy_in(conn, e->path, full->entry_data[k],
							  (size_t) e->data_len);
			}

			if (full->metadata_sql && full->metadata_sql[0])
			{
				const char *ts_marker =
					"-- === TimescaleDB hypertables ===\n";
				char	   *ts_split;

				ereport(NOTICE,
						(errmsg("applying METADATA")));

				ts_split = strstr(full->metadata_sql, ts_marker);
				if (ts_split != NULL)
				{
					size_t		head_len = (size_t) (ts_split - full->metadata_sql);
					char	   *head = pnstrdup(full->metadata_sql, head_len);
					const char *tail = ts_split + strlen(ts_marker);

					if (head[0])
						pgbu_libpq_exec(conn, head, "METADATA");
					pfree(head);

					/*
					 * Replay each timescaledb statement on its own. TS
					 * create_hypertable(..., migrate_data => true) silently
					 * skips the data move when it runs inside an implicit
					 * multi-statement transaction (PQexec batches all
					 * statements into one transaction); only the catalog
					 * registration happens. Running each statement as its
					 * own PQexec gives each one its own implicit transaction,
					 * so migrate_data actually moves rows into chunks.
					 */
					{
						const char *p = tail;
						bool		in_string = false;
						bool		in_dollar = false;
						const char *dollar_close = NULL;
						size_t		dollar_close_len = 0;
						const char *stmt_start = p;

						while (*p)
						{
							if (in_dollar)
							{
								if (*p == '$' && dollar_close_len > 0 &&
									strncmp(p, dollar_close, dollar_close_len) == 0)
								{
									p += dollar_close_len;
									in_dollar = false;
									dollar_close = NULL;
									dollar_close_len = 0;
									continue;
								}
								p++;
								continue;
							}
							if (in_string)
							{
								if (*p == '\'')
								{
									if (*(p + 1) == '\'')
									{
										p += 2;
										continue;
									}
									in_string = false;
								}
								p++;
								continue;
							}
							if (*p == '\'')
							{
								in_string = true;
								p++;
								continue;
							}
							if (*p == '$')
							{
								const char *q = p + 1;

								while (*q && *q != '$' &&
									   ((*q >= 'a' && *q <= 'z') ||
										(*q >= 'A' && *q <= 'Z') ||
										(*q >= '0' && *q <= '9') ||
										*q == '_'))
									q++;
								if (*q == '$')
								{
									dollar_close_len = (size_t) (q - p) + 1;
									dollar_close = p;
									in_dollar = true;
									p = q + 1;
									continue;
								}
								p++;
								continue;
							}
							if (*p == ';')
							{
								const char *s = stmt_start;
								size_t		slen = (size_t) (p - stmt_start) + 1;

								while (slen > 0 && (*s == ' ' || *s == '\n' ||
													*s == '\r' || *s == '\t'))
								{
									s++;
									slen--;
								}
								if (slen > 0)
								{
									char	   *one = pnstrdup(s, slen);

									pgbu_libpq_exec(conn, one,
													"METADATA (timescaledb)");
									pfree(one);
								}
								p++;
								stmt_start = p;
								continue;
							}
							p++;
						}
					}
				}
				else
				{
					pgbu_libpq_exec(conn, full->metadata_sql, "METADATA");
				}
			}

			for (f = 1; f < file_count; f++)
			{
				ParsedSimpleBackup *diff = &parsed_list[f];

				if (diff->schema_sql && diff->schema_sql[0])
					pgbu_libpq_exec_schema_idempotent(conn, diff->schema_sql);

				for (k = 0; k < diff->entry_count; k++)
				{
					BakDataEntry *e = &diff->entries[k];

					CHECK_FOR_INTERRUPTS();
					ereport(NOTICE,
							(errmsg("restoring diff table %u/%u: %s",
									k + 1, diff->entry_count, e->path)));
					libpq_truncate(conn, e->path);
					libpq_copy_in(conn, e->path, diff->entry_data[k],
								  (size_t) e->data_len);
				}

				if (diff->metadata_sql && diff->metadata_sql[0])
					pgbu_libpq_exec(conn, diff->metadata_sql, "METADATA (diff)");
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

	pfree(temp_dbname);
}
