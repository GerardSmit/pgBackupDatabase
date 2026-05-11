#include "postgres.h"
#include "miscadmin.h"

#include "executor/spi.h"
#include "utils/builtins.h"

#include "ddl_gen.h"

#define SKIP_SYSTEM_NSP_SQL \
	"n.nspname NOT LIKE 'pg\\_%' " \
	"AND n.nspname NOT IN ('information_schema', 'dbbackup', " \
	"'_timescaledb_internal', '_timescaledb_catalog', " \
	"'_timescaledb_config', '_timescaledb_cache', " \
	"'_timescaledb_functions', 'timescaledb_information', " \
	"'timescaledb_experimental')"

StringInfo
ddl_gen_schema(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT n.nspname "
		"FROM pg_namespace n "
		"WHERE " SKIP_SYSTEM_NSP_SQL " "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_depend d "
		"    WHERE d.objid = n.oid "
		"      AND d.classid = 'pg_namespace'::regclass "
		"      AND d.deptype = 'e'"
		"  ) "
		"ORDER BY n.nspname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *nspname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);

			if (nspname == NULL)
				continue;

			appendStringInfo(result,
							 "CREATE SCHEMA IF NOT EXISTS %s;\n",
							 quote_identifier(nspname));
		}
	}

	SPI_finish();
	return result;
}

StringInfo
ddl_gen_types(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();

	/* Enums */
	ret = SPI_execute(
		"SELECT n.nspname, t.typname, "
		"       array_to_string(array("
		"         SELECT quote_literal(e.enumlabel) "
		"         FROM pg_enum e "
		"         WHERE e.enumtypid = t.oid "
		"         ORDER BY e.enumsortorder"
		"       ), ', ') AS labels "
		"FROM pg_type t "
		"JOIN pg_namespace n ON t.typnamespace = n.oid "
		"WHERE t.typtype = 'e' "
		"  AND " SKIP_SYSTEM_NSP_SQL " "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_depend d "
		"    WHERE d.objid = t.oid AND d.deptype = 'e'"
		"  ) "
		"ORDER BY n.nspname, t.typname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *nspname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *typname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 2);
			char	   *labels = SPI_getvalue(SPI_tuptable->vals[i],
											  SPI_tuptable->tupdesc, 3);

			if (nspname == NULL || typname == NULL)
				continue;

			appendStringInfo(result,
							 "CREATE TYPE %s.%s AS ENUM (%s);\n",
							 quote_identifier(nspname),
							 quote_identifier(typname),
							 labels ? labels : "");
		}
	}

	/* Domains */
	ret = SPI_execute(
		"SELECT n.nspname, t.typname, "
		"       format_type(t.typbasetype, t.typtypmod) AS basetype, "
		"       t.typnotnull, "
		"       pg_get_expr(t.typdefaultbin, 0) AS default_expr "
		"FROM pg_type t "
		"JOIN pg_namespace n ON t.typnamespace = n.oid "
		"WHERE t.typtype = 'd' "
		"  AND " SKIP_SYSTEM_NSP_SQL " "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_depend d "
		"    WHERE d.objid = t.oid AND d.deptype = 'e'"
		"  ) "
		"ORDER BY n.nspname, t.typname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *nspname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *typname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 2);
			char	   *basetype = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 3);
			char	   *notnull = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 4);
			char	   *def = SPI_getvalue(SPI_tuptable->vals[i],
										   SPI_tuptable->tupdesc, 5);

			if (nspname == NULL || typname == NULL || basetype == NULL)
				continue;

			appendStringInfo(result,
							 "CREATE DOMAIN %s.%s AS %s",
							 quote_identifier(nspname),
							 quote_identifier(typname),
							 basetype);
			if (def != NULL)
				appendStringInfo(result, " DEFAULT %s", def);
			if (notnull && strcmp(notnull, "t") == 0)
				appendStringInfoString(result, " NOT NULL");
			appendStringInfoString(result, ";\n");
		}
	}

	/* Composite types (not associated with a table) */
	ret = SPI_execute(
		"SELECT n.nspname, t.typname, "
		"       array_to_string(array("
		"         SELECT quote_ident(a.attname) || ' ' || "
		"                format_type(a.atttypid, a.atttypmod) "
		"         FROM pg_attribute a "
		"         WHERE a.attrelid = t.typrelid "
		"           AND a.attnum > 0 "
		"           AND NOT a.attisdropped "
		"         ORDER BY a.attnum"
		"       ), ', ') AS attrs "
		"FROM pg_type t "
		"JOIN pg_namespace n ON t.typnamespace = n.oid "
		"JOIN pg_class c ON t.typrelid = c.oid "
		"WHERE t.typtype = 'c' "
		"  AND c.relkind = 'c' "
		"  AND " SKIP_SYSTEM_NSP_SQL " "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_depend d "
		"    WHERE d.objid = t.oid AND d.deptype = 'e'"
		"  ) "
		"ORDER BY n.nspname, t.typname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *nspname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *typname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 2);
			char	   *attrs = SPI_getvalue(SPI_tuptable->vals[i],
											 SPI_tuptable->tupdesc, 3);

			if (nspname == NULL || typname == NULL)
				continue;

			appendStringInfo(result,
							 "CREATE TYPE %s.%s AS (%s);\n",
							 quote_identifier(nspname),
							 quote_identifier(typname),
							 attrs ? attrs : "");
		}
	}

	SPI_finish();
	return result;
}

StringInfo
ddl_gen_sequences(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT n.nspname, c.relname, "
		"       format_type(s.seqtypid, NULL) AS data_type, "
		"       s.seqstart, s.seqincrement, s.seqmin, s.seqmax, "
		"       s.seqcache, s.seqcycle "
		"FROM pg_sequence s "
		"JOIN pg_class c ON s.seqrelid = c.oid "
		"JOIN pg_namespace n ON c.relnamespace = n.oid "
		"WHERE " SKIP_SYSTEM_NSP_SQL " "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_depend d "
		"    WHERE d.objid = c.oid AND d.deptype IN ('e', 'a')"
		"  ) "
		"ORDER BY n.nspname, c.relname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *nspname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *relname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 2);
			char	   *dtype = SPI_getvalue(SPI_tuptable->vals[i],
											 SPI_tuptable->tupdesc, 3);
			char	   *seqstart = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 4);
			char	   *seqinc = SPI_getvalue(SPI_tuptable->vals[i],
											  SPI_tuptable->tupdesc, 5);
			char	   *seqmin = SPI_getvalue(SPI_tuptable->vals[i],
											  SPI_tuptable->tupdesc, 6);
			char	   *seqmax = SPI_getvalue(SPI_tuptable->vals[i],
											  SPI_tuptable->tupdesc, 7);
			char	   *seqcache = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 8);
			char	   *seqcycle = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 9);

			if (nspname == NULL || relname == NULL)
				continue;

			appendStringInfo(result, "CREATE SEQUENCE %s.%s",
							 quote_identifier(nspname),
							 quote_identifier(relname));
			if (dtype != NULL)
				appendStringInfo(result, " AS %s", dtype);
			if (seqstart != NULL)
				appendStringInfo(result, " START WITH %s", seqstart);
			if (seqinc != NULL)
				appendStringInfo(result, " INCREMENT BY %s", seqinc);
			if (seqmin != NULL)
				appendStringInfo(result, " MINVALUE %s", seqmin);
			if (seqmax != NULL)
				appendStringInfo(result, " MAXVALUE %s", seqmax);
			if (seqcache != NULL)
				appendStringInfo(result, " CACHE %s", seqcache);
			if (seqcycle && strcmp(seqcycle, "t") == 0)
				appendStringInfoString(result, " CYCLE");
			appendStringInfoString(result, ";\n");
		}
	}

	SPI_finish();
	return result;
}

StringInfo
ddl_gen_tables(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT n.nspname, c.relname, c.oid, "
		"       array_to_string(array("
		"         SELECT quote_ident(a.attname) || ' ' || "
		"                format_type(a.atttypid, a.atttypmod) || "
		"                CASE WHEN a.attnotnull THEN ' NOT NULL' ELSE '' END || "
		"                CASE WHEN ad.adbin IS NOT NULL "
		"                     THEN ' DEFAULT ' || pg_get_expr(ad.adbin, ad.adrelid) "
		"                     ELSE '' END "
		"         FROM pg_attribute a "
		"         LEFT JOIN pg_attrdef ad ON ad.adrelid = a.attrelid AND ad.adnum = a.attnum "
		"         WHERE a.attrelid = c.oid "
		"           AND a.attnum > 0 "
		"           AND NOT a.attisdropped "
		"         ORDER BY a.attnum"
		"       ), ', ') AS cols "
		"FROM pg_class c "
		"JOIN pg_namespace n ON c.relnamespace = n.oid "
		"WHERE c.relkind = 'r' "
		"  AND " SKIP_SYSTEM_NSP_SQL " "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_depend d "
		"    WHERE d.objid = c.oid AND d.deptype = 'e'"
		"  ) "
		"ORDER BY n.nspname, c.relname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *nspname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *relname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 2);
			char	   *cols = SPI_getvalue(SPI_tuptable->vals[i],
											SPI_tuptable->tupdesc, 4);

			if (nspname == NULL || relname == NULL)
				continue;

			appendStringInfo(result,
							 "CREATE TABLE IF NOT EXISTS %s.%s (%s);\n",
							 quote_identifier(nspname),
							 quote_identifier(relname),
							 cols ? cols : "");
		}
	}

	SPI_finish();
	return result;
}

StringInfo
ddl_gen_constraints(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT n.nspname, c.relname, con.conname, "
		"       pg_get_constraintdef(con.oid) AS condef "
		"FROM pg_constraint con "
		"JOIN pg_class c ON con.conrelid = c.oid "
		"JOIN pg_namespace n ON c.relnamespace = n.oid "
		"WHERE con.contype IN ('p', 'u', 'f', 'c', 'x') "
		"  AND " SKIP_SYSTEM_NSP_SQL " "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_depend d "
		"    WHERE d.objid = c.oid AND d.deptype = 'e'"
		"  ) "
		"ORDER BY n.nspname, c.relname, "
		"         CASE con.contype WHEN 'p' THEN 1 WHEN 'u' THEN 2 "
		"                          WHEN 'c' THEN 3 WHEN 'x' THEN 4 "
		"                          WHEN 'f' THEN 5 ELSE 6 END, "
		"         con.conname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *nspname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *relname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 2);
			char	   *conname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 3);
			char	   *condef = SPI_getvalue(SPI_tuptable->vals[i],
											  SPI_tuptable->tupdesc, 4);

			if (nspname == NULL || relname == NULL || conname == NULL ||
				condef == NULL)
				continue;

			appendStringInfo(result,
							 "ALTER TABLE %s.%s ADD CONSTRAINT %s %s;\n",
							 quote_identifier(nspname),
							 quote_identifier(relname),
							 quote_identifier(conname),
							 condef);
		}
	}

	SPI_finish();
	return result;
}

StringInfo
ddl_gen_indexes(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT pg_get_indexdef(i.indexrelid) AS idxdef "
		"FROM pg_index i "
		"JOIN pg_class c ON i.indexrelid = c.oid "
		"JOIN pg_namespace n ON c.relnamespace = n.oid "
		"WHERE " SKIP_SYSTEM_NSP_SQL " "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_constraint con "
		"    WHERE con.conindid = i.indexrelid"
		"  ) "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_depend d "
		"    WHERE d.objid = c.oid AND d.deptype = 'e'"
		"  ) "
		"ORDER BY n.nspname, c.relname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *idxdef = SPI_getvalue(SPI_tuptable->vals[i],
											  SPI_tuptable->tupdesc, 1);

			if (idxdef == NULL)
				continue;

			appendStringInfo(result, "%s;\n", idxdef);
		}
	}

	SPI_finish();
	return result;
}

StringInfo
ddl_gen_functions(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT pg_get_functiondef(p.oid) AS fndef "
		"FROM pg_proc p "
		"JOIN pg_namespace n ON p.pronamespace = n.oid "
		"WHERE " SKIP_SYSTEM_NSP_SQL " "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_depend d "
		"    WHERE d.objid = p.oid AND d.deptype = 'e'"
		"  ) "
		"  AND p.prokind IN ('f', 'p') "
		"ORDER BY n.nspname, p.proname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *fndef = SPI_getvalue(SPI_tuptable->vals[i],
											 SPI_tuptable->tupdesc, 1);

			if (fndef == NULL)
				continue;

			appendStringInfo(result, "%s\n", fndef);
		}
	}

	SPI_finish();
	return result;
}

StringInfo
ddl_gen_views(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;
	bool		has_cagg_catalog = false;

	(void) db_oid;

	SPI_connect();

	ret = SPI_execute(
		"SELECT 1 FROM pg_class c "
		"JOIN pg_namespace n ON c.relnamespace = n.oid "
		"WHERE n.nspname = '_timescaledb_catalog' "
		"  AND c.relname = 'continuous_agg'",
		true, 0);
	if (ret == SPI_OK_SELECT && SPI_processed > 0)
		has_cagg_catalog = true;

	if (has_cagg_catalog)
	{
		ret = SPI_execute(
			"SELECT n.nspname, c.relname, "
			"       pg_get_viewdef(c.oid, true) AS viewdef "
			"FROM pg_class c "
			"JOIN pg_namespace n ON c.relnamespace = n.oid "
			"WHERE c.relkind = 'v' "
			"  AND " SKIP_SYSTEM_NSP_SQL " "
			"  AND NOT EXISTS ("
			"    SELECT 1 FROM pg_depend d "
			"    WHERE d.objid = c.oid AND d.deptype = 'e'"
			"  ) "
			"  AND NOT EXISTS ("
			"    SELECT 1 FROM _timescaledb_catalog.continuous_agg ca "
			"    WHERE ca.user_view_schema = n.nspname "
			"      AND ca.user_view_name = c.relname"
			"  ) "
			"ORDER BY n.nspname, c.relname",
			true, 0);
	}
	else
	{
		ret = SPI_execute(
			"SELECT n.nspname, c.relname, "
			"       pg_get_viewdef(c.oid, true) AS viewdef "
			"FROM pg_class c "
			"JOIN pg_namespace n ON c.relnamespace = n.oid "
			"WHERE c.relkind = 'v' "
			"  AND " SKIP_SYSTEM_NSP_SQL " "
			"  AND NOT EXISTS ("
			"    SELECT 1 FROM pg_depend d "
			"    WHERE d.objid = c.oid AND d.deptype = 'e'"
			"  ) "
			"ORDER BY n.nspname, c.relname",
			true, 0);
	}

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *nspname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *relname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 2);
			char	   *viewdef = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 3);

			if (nspname == NULL || relname == NULL || viewdef == NULL)
				continue;

			appendStringInfo(result,
							 "CREATE OR REPLACE VIEW %s.%s AS %s\n",
							 quote_identifier(nspname),
							 quote_identifier(relname),
							 viewdef);
		}
	}

	SPI_finish();
	return result;
}

StringInfo
ddl_gen_all(Oid db_oid)
{
	StringInfo	buf = makeStringInfo();
	StringInfo	part;

	appendStringInfoString(buf, "-- === Schemas ===\n");
	part = ddl_gen_schema(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Types ===\n");
	part = ddl_gen_types(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Sequences ===\n");
	part = ddl_gen_sequences(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Tables ===\n");
	part = ddl_gen_tables(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Constraints ===\n");
	part = ddl_gen_constraints(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Indexes ===\n");
	part = ddl_gen_indexes(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Functions ===\n");
	part = ddl_gen_functions(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Views ===\n");
	part = ddl_gen_views(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	return buf;
}
