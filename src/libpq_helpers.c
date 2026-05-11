#include "postgres.h"

#include <libpq-fe.h>

#include "lib/stringinfo.h"
#include "miscadmin.h"
#include "port.h"
#include "postmaster/postmaster.h"
#include "storage/latch.h"
#include "utils/builtins.h"
#include "utils/guc.h"
#include "utils/wait_event.h"

#include "libpq_helpers.h"

char *
pgbu_generate_temp_dbname(void)
{
	uint8		rnd[8];
	StringInfoData s;
	int			i;

	if (!pg_strong_random(rnd, sizeof(rnd)))
	{
		for (i = 0; i < (int) sizeof(rnd); i++)
			rnd[i] = (uint8) (random() & 0xff);
	}

	initStringInfo(&s);
	appendStringInfoString(&s, "_pg_dbbackup_restore_");
	for (i = 0; i < (int) sizeof(rnd); i++)
		appendStringInfo(&s, "%02x", rnd[i]);
	return s.data;
}

char *
pgbu_quote_db_identifier(const char *name)
{
	const char *p;
	StringInfoData s;

	initStringInfo(&s);
	appendStringInfoChar(&s, '"');
	for (p = name; *p; p++)
	{
		if (*p == '"')
			appendStringInfoChar(&s, '"');
		appendStringInfoChar(&s, *p);
	}
	appendStringInfoChar(&s, '"');
	return s.data;
}

static PGconn *
try_libpq_connect(const char *host, const char *port,
				  const char *user, const char *dbname)
{
	char	   *conninfo;
	PGconn	   *conn;

	conninfo = psprintf(
		"host=%s port=%s user=%s dbname=%s connect_timeout=5",
		host, port, user, dbname);

	conn = PQconnectdb(conninfo);
	pfree(conninfo);

	if (PQstatus(conn) != CONNECTION_OK)
	{
		PQfinish(conn);
		return NULL;
	}
	return conn;
}

/*
 * Return a freshly palloc'd copy of the first non-empty entry of the
 * unix_socket_directories GUC, or NULL if the GUC is unset/empty.
 */
static char *
first_socket_dir(void)
{
	const char *guc_val;
	const char *p;
	const char *comma;
	char	   *entry;
	size_t		len;

	guc_val = GetConfigOption("unix_socket_directories", true, false);
	if (guc_val == NULL)
		return NULL;

	p = guc_val;
	while (*p)
	{
		while (*p == ' ' || *p == '\t')
			p++;
		comma = strchr(p, ',');
		len = comma ? (size_t) (comma - p) : strlen(p);

		while (len > 0 && (p[len - 1] == ' ' || p[len - 1] == '\t'))
			len--;

		if (len > 0)
		{
			entry = pnstrdup(p, len);
			return entry;
		}

		if (!comma)
			break;
		p = comma + 1;
	}

	return NULL;
}

PGconn *
pgbu_connect_libpq(const char *dbname)
{
	char	   *socket_dir;
	const char *user;
	char		port_str[16];
	PGconn	   *conn = NULL;

	socket_dir = first_socket_dir();
	user = GetUserNameFromId(GetUserId(), false);
	snprintf(port_str, sizeof(port_str), "%d",
			 PostPortNumber > 0 ? PostPortNumber : 5432);

	if (socket_dir != NULL)
	{
		conn = try_libpq_connect(socket_dir, port_str, user, dbname);
		pfree(socket_dir);
	}

	if (conn == NULL)
		conn = try_libpq_connect("localhost", port_str, user, dbname);

	if (conn == NULL)
		ereport(ERROR,
				(errcode(ERRCODE_CONNECTION_FAILURE),
				 errmsg("could not connect via libpq to database \"%s\"",
						dbname),
				 errdetail("Tried Unix socket from unix_socket_directories and TCP localhost on port %s.",
						   port_str)));

	return conn;
}

PGresult *
pgbu_libpq_exec_interruptible(PGconn *conn, const char *sql)
{
	int			sock;
	PGresult   *result = NULL;
	PGresult   *last = NULL;

	if (!PQsendQuery(conn, sql))
		return NULL;

	sock = PQsocket(conn);
	if (sock < 0)
		return NULL;

	while (PQisBusy(conn))
	{
		WaitEvent	evt;
		WaitEventSet *wes;

		CHECK_FOR_INTERRUPTS();

		wes = CreateWaitEventSet(NULL, 2);
		AddWaitEventToSet(wes, WL_SOCKET_READABLE, sock, NULL, NULL);
		AddWaitEventToSet(wes, WL_EXIT_ON_PM_DEATH, PGINVALID_SOCKET,
						  NULL, NULL);

		(void) WaitEventSetWait(wes, 1000L, &evt, 1, PG_WAIT_EXTENSION);

		FreeWaitEventSet(wes);

		if (PQconsumeInput(conn) == 0)
			return NULL;
	}

	while ((result = PQgetResult(conn)) != NULL)
	{
		if (last)
			PQclear(last);
		last = result;
	}
	return last;
}

void
pgbu_libpq_exec(PGconn *conn, const char *sql, const char *what)
{
	PGresult   *res = pgbu_libpq_exec_interruptible(conn, sql);
	ExecStatusType st;

	if (res == NULL)
	{
		char	   *msg = pstrdup(PQerrorMessage(conn));

		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("libpq exec failed during %s (no result)", what),
				 errdetail("%s", msg)));
	}

	st = PQresultStatus(res);
	if (st != PGRES_COMMAND_OK && st != PGRES_TUPLES_OK)
	{
		char	   *msg = pstrdup(PQerrorMessage(conn));

		PQclear(res);
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("libpq exec failed during %s", what),
				 errdetail("%s", msg)));
	}
	PQclear(res);
}

static void
append_sql_escaped(StringInfo out, const char *s, size_t slen)
{
	size_t		i;

	for (i = 0; i < slen; i++)
	{
		char		c = s[i];

		if (c == '\'')
			appendStringInfoChar(out, '\'');
		appendStringInfoChar(out, c);
	}
}

/*
 * Reapply a SCHEMA script idempotently. We split on top-level semicolons
 * (skipping those inside single-quoted strings and $tag$ blocks) and emit
 * each statement as its own BEGIN/EXCEPTION/END subblock inside a single
 * DO. A duplicate-object failure in one subblock does not abort the batch.
 */
void
pgbu_libpq_exec_schema_idempotent(PGconn *conn, const char *script)
{
	const char *p;
	const char *stmt_start;
	StringInfoData composite;
	bool		in_string = false;
	bool		in_dollar = false;
	const char *dollar_close = NULL;
	size_t		dollar_close_len = 0;
	size_t		emitted = 0;

	if (script == NULL || script[0] == '\0')
		return;

	initStringInfo(&composite);
	appendStringInfoString(&composite, "DO $pgdb_outer$\nBEGIN\n");

	p = script;
	stmt_start = p;

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
				   ((*q >= 'a' && *q <= 'z') || (*q >= 'A' && *q <= 'Z') ||
					(*q >= '0' && *q <= '9') || *q == '_'))
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
			size_t		slen = (size_t) (p - stmt_start);

			while (slen > 0 && (*s == ' ' || *s == '\n' || *s == '\r' ||
								*s == '\t'))
			{
				s++;
				slen--;
			}
			while (slen > 0 && (s[slen - 1] == ' ' || s[slen - 1] == '\n' ||
								s[slen - 1] == '\r' || s[slen - 1] == '\t'))
				slen--;

			if (slen > 0)
			{
				appendStringInfoString(&composite, "  BEGIN EXECUTE '");
				append_sql_escaped(&composite, s, slen);
				appendStringInfoString(&composite,
									   "'; EXCEPTION WHEN OTHERS THEN NULL; END;\n");
				emitted++;
			}

			p++;
			stmt_start = p;
			continue;
		}

		p++;
	}

	appendStringInfoString(&composite, "END\n$pgdb_outer$;");

	if (emitted > 0)
	{
		PGresult   *res = PQexec(conn, composite.data);

		if (res == NULL)
		{
			char	   *msg = pstrdup(PQerrorMessage(conn));

			pfree(composite.data);
			ereport(ERROR,
					(errcode(ERRCODE_INTERNAL_ERROR),
					 errmsg("DIFF SCHEMA reapply failed (no result)"),
					 errdetail("%s", msg)));
		}
		else
		{
			ExecStatusType st = PQresultStatus(res);

			if (st != PGRES_COMMAND_OK && st != PGRES_TUPLES_OK)
			{
				char	   *msg = pstrdup(PQerrorMessage(conn));

				PQclear(res);
				pfree(composite.data);
				ereport(ERROR,
						(errcode(ERRCODE_INTERNAL_ERROR),
						 errmsg("DIFF SCHEMA reapply failed"),
						 errdetail("%s", msg)));
			}
			PQclear(res);
		}
	}

	pfree(composite.data);
}

void
pgbu_drop_temp_db_quiet(const char *temp_dbname)
{
	PGconn	   *admin = NULL;
	char	   *sql;
	PGresult   *res;

	PG_TRY();
	{
		admin = pgbu_connect_libpq("postgres");
	}
	PG_CATCH();
	{
		FlushErrorState();
		return;
	}
	PG_END_TRY();

	sql = psprintf("DROP DATABASE IF EXISTS %s WITH (FORCE)",
				   pgbu_quote_db_identifier(temp_dbname));
	res = PQexec(admin, sql);
	if (res)
		PQclear(res);
	pfree(sql);
	PQfinish(admin);
}
