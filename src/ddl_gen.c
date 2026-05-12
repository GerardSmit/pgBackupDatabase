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
		"       pg_get_expr(t.typdefaultbin, 0) AS default_expr, "
		"       array_to_string(array("
		"         SELECT ' CONSTRAINT ' || quote_ident(con.conname) || "
		"                ' ' || pg_get_constraintdef(con.oid) "
		"         FROM pg_constraint con "
		"         WHERE con.contypid = t.oid "
		"         ORDER BY con.conname"
		"       ), '') AS constraints_sql "
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
			char	   *constraints = SPI_getvalue(SPI_tuptable->vals[i],
												   SPI_tuptable->tupdesc, 6);

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
			if (constraints != NULL)
				appendStringInfoString(result, constraints);
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
	"    WHERE d.objid = c.oid AND d.deptype IN ('e', 'i')"
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
		"SELECT n.nspname, c.relname, c.oid, c.relkind, c.relispartition, "
		"       pn.nspname AS parent_schema, pc.relname AS parent_name, "
		"       pg_get_expr(c.relpartbound, c.oid) AS part_bound, "
		"       CASE WHEN c.relkind = 'p' THEN pg_get_partkeydef(c.oid) "
		"            ELSE NULL END AS part_key, "
		"       array_to_string(array("
		"         SELECT quote_ident(a.attname) || ' ' || "
		"                format_type(a.atttypid, a.atttypmod) || "
		"                CASE WHEN a.attcollation <> 0 "
		"                       AND a.attcollation <> typ.typcollation "
		"                       AND coll.oid IS NOT NULL "
		"                     THEN ' COLLATE ' || "
		"                          quote_ident(colln.nspname) || '.' || "
		"                          quote_ident(coll.collname) "
		"                     ELSE '' END || "
		"                CASE WHEN a.attidentity = 'a' "
		"                     THEN ' GENERATED ALWAYS AS IDENTITY' "
		"                     WHEN a.attidentity = 'd' "
		"                     THEN ' GENERATED BY DEFAULT AS IDENTITY' "
		"                     WHEN a.attgenerated = 's' "
		"                     THEN ' GENERATED ALWAYS AS (' || "
		"                          pg_get_expr(ad.adbin, ad.adrelid) || "
		"                          ') STORED' "
		"                     WHEN a.attgenerated = 'v' "
		"                     THEN ' GENERATED ALWAYS AS (' || "
		"                          pg_get_expr(ad.adbin, ad.adrelid) || "
		"                          ') VIRTUAL' "
		"                     WHEN ad.adbin IS NOT NULL "
		"                     THEN ' DEFAULT ' || pg_get_expr(ad.adbin, ad.adrelid) "
		"                     ELSE '' END "
		"                || CASE WHEN a.attnotnull THEN ' NOT NULL' ELSE '' END "
		"         FROM pg_attribute a "
		"         JOIN pg_type typ ON typ.oid = a.atttypid "
		"         LEFT JOIN pg_attrdef ad ON ad.adrelid = a.attrelid AND ad.adnum = a.attnum "
		"         LEFT JOIN pg_collation coll ON coll.oid = a.attcollation "
		"         LEFT JOIN pg_namespace colln ON colln.oid = coll.collnamespace "
		"         WHERE a.attrelid = c.oid "
		"           AND a.attnum > 0 "
		"           AND NOT a.attisdropped "
		"         ORDER BY a.attnum"
		"       ), ', ') AS cols "
		"FROM pg_class c "
		"JOIN pg_namespace n ON c.relnamespace = n.oid "
		"LEFT JOIN pg_inherits inh ON inh.inhrelid = c.oid "
		"LEFT JOIN pg_class pc ON pc.oid = inh.inhparent "
		"LEFT JOIN pg_namespace pn ON pn.oid = pc.relnamespace "
		"WHERE c.relkind IN ('r', 'p') "
		"  AND " SKIP_SYSTEM_NSP_SQL " "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_depend d "
		"    WHERE d.objid = c.oid AND d.deptype = 'e'"
		"  ) "
		"ORDER BY CASE WHEN c.relkind = 'p' THEN 0 "
		"              WHEN c.relispartition THEN 2 ELSE 1 END, "
		"         n.nspname, c.relname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *nspname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *relname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 2);
			char	   *relkind = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 4);
			char	   *relispartition = SPI_getvalue(SPI_tuptable->vals[i],
													 SPI_tuptable->tupdesc, 5);
			char	   *parent_schema = SPI_getvalue(SPI_tuptable->vals[i],
													 SPI_tuptable->tupdesc, 6);
			char	   *parent_name = SPI_getvalue(SPI_tuptable->vals[i],
												   SPI_tuptable->tupdesc, 7);
			char	   *part_bound = SPI_getvalue(SPI_tuptable->vals[i],
												  SPI_tuptable->tupdesc, 8);
			char	   *part_key = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 9);
			char	   *cols = SPI_getvalue(SPI_tuptable->vals[i],
											SPI_tuptable->tupdesc, 10);

			if (nspname == NULL || relname == NULL)
				continue;

			if (relispartition != NULL && relispartition[0] == 't')
			{
				if (parent_schema == NULL || parent_name == NULL ||
					part_bound == NULL)
					continue;

				appendStringInfo(result,
								 "CREATE TABLE IF NOT EXISTS %s.%s "
								 "PARTITION OF %s.%s %s;\n",
								 quote_identifier(nspname),
								 quote_identifier(relname),
								 quote_identifier(parent_schema),
								 quote_identifier(parent_name),
								 part_bound);
			}
			else if (relkind != NULL && relkind[0] == 'p')
			{
				appendStringInfo(result,
								 "CREATE TABLE IF NOT EXISTS %s.%s (%s) "
								 "PARTITION BY %s;\n",
								 quote_identifier(nspname),
								 quote_identifier(relname),
								 cols ? cols : "",
								 part_key ? part_key : "");
			}
			else
			{
				appendStringInfo(result,
								 "CREATE TABLE IF NOT EXISTS %s.%s (%s);\n",
								 quote_identifier(nspname),
								 quote_identifier(relname),
								 cols ? cols : "");
			}
		}
	}

	SPI_finish();
	return result;
}

StringInfo
ddl_gen_sequence_ownership(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT sn.nspname, s.relname, tn.nspname, t.relname, a.attname "
		"FROM pg_class s "
		"JOIN pg_namespace sn ON s.relnamespace = sn.oid "
		"JOIN pg_depend d ON d.objid = s.oid "
		"JOIN pg_class t ON t.oid = d.refobjid "
		"JOIN pg_namespace tn ON t.relnamespace = tn.oid "
		"JOIN pg_attribute a ON a.attrelid = t.oid "
		"                   AND a.attnum = d.refobjsubid "
		"WHERE s.relkind = 'S' "
		"  AND d.deptype = 'a' "
		"  AND sn.nspname NOT LIKE 'pg\\_%' "
		"  AND tn.nspname NOT LIKE 'pg\\_%' "
		"  AND sn.nspname NOT IN ('information_schema', 'dbbackup') "
		"  AND tn.nspname NOT IN ('information_schema', 'dbbackup') "
		"ORDER BY sn.nspname, s.relname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *seq_nsp = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *seq_rel = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 2);
			char	   *tbl_nsp = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 3);
			char	   *tbl_rel = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 4);
			char	   *attname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 5);

			if (seq_nsp == NULL || seq_rel == NULL || tbl_nsp == NULL ||
				tbl_rel == NULL || attname == NULL)
				continue;

			appendStringInfo(result,
							 "ALTER SEQUENCE %s.%s OWNED BY %s.%s.%s;\n",
							 quote_identifier(seq_nsp),
							 quote_identifier(seq_rel),
							 quote_identifier(tbl_nsp),
							 quote_identifier(tbl_rel),
							 quote_identifier(attname));
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
	"  AND NOT c.relispartition "
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
	"    SELECT 1 FROM pg_inherits inh "
	"    WHERE inh.inhrelid = i.indexrelid"
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
			size_t		len;

			if (fndef == NULL)
				continue;

			len = strlen(fndef);
			appendStringInfoString(result, fndef);
			if (len == 0 || fndef[len - 1] != ';')
				appendStringInfoChar(result, ';');
			appendStringInfoChar(result, '\n');
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
ddl_gen_rules(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT pg_get_ruledef(r.oid, false) AS ruledef "
		"FROM pg_rewrite r "
		"JOIN pg_class c ON r.ev_class = c.oid "
		"JOIN pg_namespace n ON c.relnamespace = n.oid "
		"WHERE r.rulename <> '_RETURN' "
		"  AND " SKIP_SYSTEM_NSP_SQL " "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_depend d "
		"    WHERE d.objid = r.oid AND d.deptype = 'e'"
		"  ) "
		"ORDER BY n.nspname, c.relname, r.rulename",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *ruledef = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			size_t		len;

			if (ruledef == NULL)
				continue;

			len = strlen(ruledef);
			appendStringInfoString(result, ruledef);
			if (len == 0 || ruledef[len - 1] != ';')
				appendStringInfoChar(result, ';');
			appendStringInfoChar(result, '\n');
		}
	}

	SPI_finish();
	return result;
}

StringInfo
ddl_gen_triggers(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT n.nspname, c.relname, t.tgname, "
		"       pg_get_triggerdef(t.oid, false) AS tgdef, "
		"       t.tgenabled "
		"FROM pg_trigger t "
		"JOIN pg_class c ON t.tgrelid = c.oid "
		"JOIN pg_namespace n ON c.relnamespace = n.oid "
		"WHERE NOT t.tgisinternal "
		"  AND t.tgparentid = 0 "
		"  AND " SKIP_SYSTEM_NSP_SQL " "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_depend d "
		"    WHERE d.objid = t.oid AND d.deptype = 'e'"
		"  ) "
		"ORDER BY n.nspname, c.relname, t.tgname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *nspname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *relname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 2);
			char	   *tgname = SPI_getvalue(SPI_tuptable->vals[i],
											  SPI_tuptable->tupdesc, 3);
			char	   *tgdef = SPI_getvalue(SPI_tuptable->vals[i],
											 SPI_tuptable->tupdesc, 4);
			char	   *tgenabled = SPI_getvalue(SPI_tuptable->vals[i],
												 SPI_tuptable->tupdesc, 5);

			if (nspname == NULL || relname == NULL ||
				tgname == NULL || tgdef == NULL)
				continue;

			appendStringInfo(result, "%s;\n", tgdef);
			if (tgenabled != NULL && tgenabled[0] != 'O')
			{
				const char *state = "ENABLE";

				switch (tgenabled[0])
				{
					case 'D':
						state = "DISABLE";
						break;
					case 'R':
						state = "ENABLE REPLICA";
						break;
					case 'A':
						state = "ENABLE ALWAYS";
						break;
					default:
						state = "ENABLE";
						break;
				}

				appendStringInfo(result,
								 "ALTER TABLE %s.%s %s TRIGGER %s;\n",
								 quote_identifier(nspname),
								 quote_identifier(relname),
								 state,
								 quote_identifier(tgname));
			}
		}
	}

	SPI_finish();
	return result;
}

StringInfo
ddl_gen_row_security(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT n.nspname, c.relname, c.relrowsecurity, c.relforcerowsecurity "
		"FROM pg_class c "
		"JOIN pg_namespace n ON c.relnamespace = n.oid "
		"WHERE c.relkind IN ('r', 'p') "
		"  AND " SKIP_SYSTEM_NSP_SQL " "
		"  AND (c.relrowsecurity OR c.relforcerowsecurity) "
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
			char	   *rowsec = SPI_getvalue(SPI_tuptable->vals[i],
											  SPI_tuptable->tupdesc, 3);
			char	   *forcerowsec = SPI_getvalue(SPI_tuptable->vals[i],
												   SPI_tuptable->tupdesc, 4);

			if (nspname == NULL || relname == NULL)
				continue;

			if (rowsec != NULL && rowsec[0] == 't')
				appendStringInfo(result,
								 "ALTER TABLE %s.%s ENABLE ROW LEVEL SECURITY;\n",
								 quote_identifier(nspname),
								 quote_identifier(relname));
			if (forcerowsec != NULL && forcerowsec[0] == 't')
				appendStringInfo(result,
								 "ALTER TABLE %s.%s FORCE ROW LEVEL SECURITY;\n",
								 quote_identifier(nspname),
								 quote_identifier(relname));
		}
	}

	ret = SPI_execute(
		"SELECT n.nspname, c.relname, pol.polname, pol.polcmd, "
		"       pol.polpermissive, "
		"       ("
		"         SELECT string_agg(role_name, ', ' ORDER BY role_name) "
		"         FROM ("
		"           SELECT CASE WHEN role_oid = 0 THEN 'PUBLIC' "
		"                       ELSE quote_ident(pg_get_userbyid(role_oid)) "
		"                  END AS role_name "
		"           FROM unnest(pol.polroles) AS role_oid"
		"         ) roles"
		"       ) AS roles, "
		"       pg_get_expr(pol.polqual, pol.polrelid) AS using_expr, "
		"       pg_get_expr(pol.polwithcheck, pol.polrelid) AS check_expr "
		"FROM pg_policy pol "
		"JOIN pg_class c ON pol.polrelid = c.oid "
		"JOIN pg_namespace n ON c.relnamespace = n.oid "
		"WHERE " SKIP_SYSTEM_NSP_SQL " "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_depend d "
		"    WHERE d.objid = pol.oid AND d.deptype = 'e'"
		"  ) "
		"ORDER BY n.nspname, c.relname, pol.polname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *nspname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *relname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 2);
			char	   *polname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 3);
			char	   *polcmd = SPI_getvalue(SPI_tuptable->vals[i],
											  SPI_tuptable->tupdesc, 4);
			char	   *permissive = SPI_getvalue(SPI_tuptable->vals[i],
												  SPI_tuptable->tupdesc, 5);
			char	   *roles = SPI_getvalue(SPI_tuptable->vals[i],
											 SPI_tuptable->tupdesc, 6);
			char	   *using_expr = SPI_getvalue(SPI_tuptable->vals[i],
												  SPI_tuptable->tupdesc, 7);
			char	   *check_expr = SPI_getvalue(SPI_tuptable->vals[i],
												  SPI_tuptable->tupdesc, 8);
			const char *cmd = "";

			if (nspname == NULL || relname == NULL ||
				polname == NULL || polcmd == NULL)
				continue;

			switch (polcmd[0])
			{
				case 'r': cmd = " FOR SELECT"; break;
				case 'a': cmd = " FOR INSERT"; break;
				case 'w': cmd = " FOR UPDATE"; break;
				case 'd': cmd = " FOR DELETE"; break;
				default: cmd = ""; break;
			}

			appendStringInfo(result, "CREATE POLICY %s ON %s.%s",
							 quote_identifier(polname),
							 quote_identifier(nspname),
							 quote_identifier(relname));
			if (permissive != NULL && permissive[0] == 'f')
				appendStringInfoString(result, " AS RESTRICTIVE");
			appendStringInfoString(result, cmd);
			if (roles != NULL && roles[0] != '\0')
				appendStringInfo(result, " TO %s", roles);
			if (using_expr != NULL)
				appendStringInfo(result, " USING (%s)", using_expr);
			if (check_expr != NULL)
				appendStringInfo(result, " WITH CHECK (%s)", check_expr);
			appendStringInfoString(result, ";\n");
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

	appendStringInfoString(buf, "-- === Sequence ownership ===\n");
	part = ddl_gen_sequence_ownership(db_oid);
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

	appendStringInfoString(buf, "-- === Rules ===\n");
	part = ddl_gen_rules(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Triggers ===\n");
	part = ddl_gen_triggers(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Row security ===\n");
	part = ddl_gen_row_security(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	return buf;
}
