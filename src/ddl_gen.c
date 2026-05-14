#include "postgres.h"
#include "miscadmin.h"

#include "executor/spi.h"
#include "utils/builtins.h"

#include "ddl_gen.h"
#include "pg_dbbackup.h"

#define SKIP_SYSTEM_NSP_SQL PGDBBACKUP_SKIP_SYSTEM_NSP

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
		"         WHERE con.contypid = t.oid AND con.contype <> 'n' "
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

	/* Range types (multirange is created automatically by CREATE TYPE AS RANGE) */
	ret = SPI_execute(
		"SELECT n.nspname, t.typname, "
		"       format_type(r.rngsubtype, NULL) AS subtype, "
		"       opcn.nspname AS opcnsp, opc.opcname, "
		"       coll.collname, colln.nspname AS collnsp, "
		"       cnsp.nspname AS canon_nsp, cproc.proname AS canon_name, "
		"       dnsp.nspname AS diff_nsp, dproc.proname AS diff_name, "
		"       mt.typname AS multirange_name "
		"FROM pg_type t "
		"JOIN pg_namespace n ON t.typnamespace = n.oid "
		"JOIN pg_range r ON r.rngtypid = t.oid "
		"LEFT JOIN pg_opclass opc ON opc.oid = r.rngsubopc "
		"LEFT JOIN pg_namespace opcn ON opcn.oid = opc.opcnamespace "
		"LEFT JOIN pg_collation coll ON coll.oid = r.rngcollation "
		"LEFT JOIN pg_namespace colln ON colln.oid = coll.collnamespace "
		"LEFT JOIN pg_proc cproc ON cproc.oid = r.rngcanonical "
		"LEFT JOIN pg_namespace cnsp ON cnsp.oid = cproc.pronamespace "
		"LEFT JOIN pg_proc dproc ON dproc.oid = r.rngsubdiff "
		"LEFT JOIN pg_namespace dnsp ON dnsp.oid = dproc.pronamespace "
		"LEFT JOIN pg_type mt ON mt.oid = r.rngmultitypid "
		"WHERE t.typtype = 'r' "
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
			char	   *subtype = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 3);
			char	   *opcnsp = SPI_getvalue(SPI_tuptable->vals[i],
											  SPI_tuptable->tupdesc, 4);
			char	   *opcname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 5);
			char	   *collname = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 6);
			char	   *collnsp = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 7);
			char	   *canon_nsp = SPI_getvalue(SPI_tuptable->vals[i],
												 SPI_tuptable->tupdesc, 8);
			char	   *canon_name = SPI_getvalue(SPI_tuptable->vals[i],
												  SPI_tuptable->tupdesc, 9);
			char	   *diff_nsp = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 10);
			char	   *diff_name = SPI_getvalue(SPI_tuptable->vals[i],
												 SPI_tuptable->tupdesc, 11);
			char	   *multirange_name = SPI_getvalue(SPI_tuptable->vals[i],
													   SPI_tuptable->tupdesc, 12);
			bool		first = true;

			if (nspname == NULL || typname == NULL || subtype == NULL)
				continue;

			appendStringInfo(result,
							 "CREATE TYPE %s.%s AS RANGE (",
							 quote_identifier(nspname),
							 quote_identifier(typname));
			appendStringInfo(result, "subtype = %s", subtype);
			first = false;
			if (opcname != NULL && opcnsp != NULL)
				appendStringInfo(result, ", subtype_opclass = %s.%s",
								 quote_identifier(opcnsp),
								 quote_identifier(opcname));
			if (collname != NULL && collnsp != NULL)
				appendStringInfo(result, ", collation = %s.%s",
								 quote_identifier(collnsp),
								 quote_identifier(collname));
			if (canon_name != NULL && canon_nsp != NULL)
				appendStringInfo(result, ", canonical = %s.%s",
								 quote_identifier(canon_nsp),
								 quote_identifier(canon_name));
			if (diff_name != NULL && diff_nsp != NULL)
				appendStringInfo(result, ", subtype_diff = %s.%s",
								 quote_identifier(diff_nsp),
								 quote_identifier(diff_name));
			if (multirange_name != NULL)
				appendStringInfo(result, ", multirange_type_name = %s",
								 quote_identifier(multirange_name));
			(void) first;
			appendStringInfoString(result, ");\n");
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
		"       ), ', ') AS cols, "
		"       array_to_string(c.reloptions, ', ') AS reloptions "
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
			char	   *reloptions = SPI_getvalue(SPI_tuptable->vals[i],
												  SPI_tuptable->tupdesc, 11);

			if (nspname == NULL || relname == NULL)
				continue;

			if (relispartition != NULL && relispartition[0] == 't')
			{
				if (parent_schema == NULL || parent_name == NULL ||
					part_bound == NULL)
					continue;

				appendStringInfo(result,
								 "CREATE TABLE IF NOT EXISTS %s.%s "
								 "PARTITION OF %s.%s %s",
								 quote_identifier(nspname),
								 quote_identifier(relname),
								 quote_identifier(parent_schema),
								 quote_identifier(parent_name),
								 part_bound);
				if (reloptions != NULL && reloptions[0] != '\0')
					appendStringInfo(result, " WITH (%s)", reloptions);
				appendStringInfoString(result, ";\n");
			}
			else if (relkind != NULL && relkind[0] == 'p')
			{
				appendStringInfo(result,
								 "CREATE TABLE IF NOT EXISTS %s.%s (%s)",
								 quote_identifier(nspname),
								 quote_identifier(relname),
								 cols ? cols : "");
				if (reloptions != NULL && reloptions[0] != '\0')
					appendStringInfo(result, " WITH (%s)", reloptions);
				appendStringInfo(result, " PARTITION BY %s;\n",
								 part_key ? part_key : "");
			}
			else
			{
				appendStringInfo(result,
								 "CREATE TABLE IF NOT EXISTS %s.%s (%s)",
								 quote_identifier(nspname),
								 quote_identifier(relname),
								 cols ? cols : "");
				if (reloptions != NULL && reloptions[0] != '\0')
					appendStringInfo(result, " WITH (%s)", reloptions);
				appendStringInfoString(result, ";\n");
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
		"ORDER BY CASE con.contype WHEN 'p' THEN 1 WHEN 'u' THEN 2 "
		"                          WHEN 'c' THEN 3 WHEN 'x' THEN 4 "
		"                          WHEN 'f' THEN 5 ELSE 6 END, "
		"         n.nspname, c.relname, con.conname",
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
ddl_gen_materialized_views(Oid db_oid)
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
			"WHERE c.relkind = 'm' "
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
			"WHERE c.relkind = 'm' "
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

			size_t		vlen;

			if (nspname == NULL || relname == NULL || viewdef == NULL)
				continue;

			/* pg_get_viewdef returns the body terminated by ';'. Strip the
			 * trailing semicolon so we can append the WITH NO DATA clause.
			 * The materialized view is intentionally created empty: data
			 * is loaded by COPY into base tables AFTER the SCHEMA section
			 * runs, so a REFRESH here would always produce empty results.
			 * Operators must run REFRESH MATERIALIZED VIEW post-restore.
			 */
			vlen = strlen(viewdef);
			while (vlen > 0 &&
				   (viewdef[vlen - 1] == ';' ||
					viewdef[vlen - 1] == '\n' ||
					viewdef[vlen - 1] == ' '))
				viewdef[--vlen] = '\0';

			appendStringInfo(result,
							 "CREATE MATERIALIZED VIEW IF NOT EXISTS %s.%s AS %s WITH NO DATA;\n",
							 quote_identifier(nspname),
							 quote_identifier(relname),
							 viewdef);
		}
	}

	SPI_finish();
	return result;
}

StringInfo
ddl_gen_inheritance(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT cn.nspname AS child_nsp, c.relname AS child_rel, "
		"       pn.nspname AS parent_nsp, p.relname AS parent_rel "
		"FROM pg_inherits i "
		"JOIN pg_class c ON c.oid = i.inhrelid "
		"JOIN pg_namespace cn ON cn.oid = c.relnamespace "
		"JOIN pg_class p ON p.oid = i.inhparent "
		"JOIN pg_namespace pn ON pn.oid = p.relnamespace "
		"WHERE c.relispartition = false "
		"  AND p.relkind <> 'p' "
		"  AND cn.nspname NOT LIKE 'pg\\_%' "
		"  AND cn.nspname NOT IN ('information_schema', 'dbbackup', "
		"      '_timescaledb_internal', '_timescaledb_catalog', "
		"      '_timescaledb_config', '_timescaledb_cache', "
		"      '_timescaledb_functions', 'timescaledb_information', "
		"      'timescaledb_experimental') "
		"  AND pn.nspname NOT LIKE 'pg\\_%' "
		"  AND pn.nspname NOT IN ('information_schema', 'dbbackup', "
		"      '_timescaledb_internal', '_timescaledb_catalog', "
		"      '_timescaledb_config', '_timescaledb_cache', "
		"      '_timescaledb_functions', 'timescaledb_information', "
		"      'timescaledb_experimental') "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_depend d "
		"    WHERE d.objid = c.oid AND d.deptype = 'e'"
		"  ) "
		"ORDER BY pn.nspname, p.relname, i.inhseqno",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *child_nsp = SPI_getvalue(SPI_tuptable->vals[i],
												 SPI_tuptable->tupdesc, 1);
			char	   *child_rel = SPI_getvalue(SPI_tuptable->vals[i],
												 SPI_tuptable->tupdesc, 2);
			char	   *parent_nsp = SPI_getvalue(SPI_tuptable->vals[i],
												  SPI_tuptable->tupdesc, 3);
			char	   *parent_rel = SPI_getvalue(SPI_tuptable->vals[i],
												  SPI_tuptable->tupdesc, 4);

			if (child_nsp == NULL || child_rel == NULL ||
				parent_nsp == NULL || parent_rel == NULL)
				continue;

			appendStringInfo(result,
							 "ALTER TABLE %s.%s INHERIT %s.%s;\n",
							 quote_identifier(child_nsp),
							 quote_identifier(child_rel),
							 quote_identifier(parent_nsp),
							 quote_identifier(parent_rel));
		}
	}

	SPI_finish();
	return result;
}

StringInfo
ddl_gen_aggregates(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT n.nspname, p.proname, "
		"       pg_get_function_arguments(p.oid) AS args, "
		"       format_type(a.aggtranstype, NULL) AS state_type, "
		"       sn.nspname AS sfunc_nsp, sf.proname AS sfunc_name, "
		"       fn.nspname AS finalfunc_nsp, ff.proname AS finalfunc_name, "
		"       format_type(p.prorettype, NULL) AS return_type, "
		"       a.agginitval "
		"FROM pg_aggregate a "
		"JOIN pg_proc p ON p.oid = a.aggfnoid "
		"JOIN pg_namespace n ON n.oid = p.pronamespace "
		"JOIN pg_proc sf ON sf.oid = a.aggtransfn "
		"JOIN pg_namespace sn ON sn.oid = sf.pronamespace "
		"LEFT JOIN pg_proc ff ON ff.oid = NULLIF(a.aggfinalfn, 0) "
		"LEFT JOIN pg_namespace fn ON fn.oid = ff.pronamespace "
		"WHERE " SKIP_SYSTEM_NSP_SQL " "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_depend d "
		"    WHERE d.objid = p.oid AND d.deptype = 'e'"
		"  ) "
		"ORDER BY n.nspname, p.proname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *nspname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *proname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 2);
			char	   *args = SPI_getvalue(SPI_tuptable->vals[i],
											SPI_tuptable->tupdesc, 3);
			char	   *state_type = SPI_getvalue(SPI_tuptable->vals[i],
												  SPI_tuptable->tupdesc, 4);
			char	   *sfunc_nsp = SPI_getvalue(SPI_tuptable->vals[i],
												 SPI_tuptable->tupdesc, 5);
			char	   *sfunc_name = SPI_getvalue(SPI_tuptable->vals[i],
												  SPI_tuptable->tupdesc, 6);
			char	   *finalfunc_nsp = SPI_getvalue(SPI_tuptable->vals[i],
													 SPI_tuptable->tupdesc, 7);
			char	   *finalfunc_name = SPI_getvalue(SPI_tuptable->vals[i],
													  SPI_tuptable->tupdesc, 8);
			char	   *initval = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 10);

			if (nspname == NULL || proname == NULL || state_type == NULL ||
				sfunc_nsp == NULL || sfunc_name == NULL)
				continue;

			appendStringInfo(result,
							 "CREATE AGGREGATE %s.%s(%s) (",
							 quote_identifier(nspname),
							 quote_identifier(proname),
							 args ? args : "");
			appendStringInfo(result, "SFUNC = %s.%s, STYPE = %s",
							 quote_identifier(sfunc_nsp),
							 quote_identifier(sfunc_name),
							 state_type);
			if (finalfunc_name != NULL && finalfunc_nsp != NULL)
				appendStringInfo(result, ", FINALFUNC = %s.%s",
								 quote_identifier(finalfunc_nsp),
								 quote_identifier(finalfunc_name));
			if (initval != NULL)
				appendStringInfo(result, ", INITCOND = %s",
								 quote_literal_cstr(initval));
			appendStringInfoString(result, ");\n");
		}
	}

	SPI_finish();
	return result;
}

StringInfo
ddl_gen_text_search_configs(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();

	/* Custom dictionaries first (configs may reference them). */
	ret = SPI_execute(
		"SELECT n.nspname, d.dictname, "
		"       tn.nspname AS tmpl_nsp, t.tmplname, "
		"       d.dictinitoption "
		"FROM pg_ts_dict d "
		"JOIN pg_namespace n ON d.dictnamespace = n.oid "
		"JOIN pg_ts_template t ON d.dicttemplate = t.oid "
		"JOIN pg_namespace tn ON t.tmplnamespace = tn.oid "
		"WHERE " SKIP_SYSTEM_NSP_SQL " "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_depend d2 "
		"    WHERE d2.objid = d.oid AND d2.deptype = 'e'"
		"  ) "
		"ORDER BY n.nspname, d.dictname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *nspname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *dictname = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 2);
			char	   *tmpl_nsp = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 3);
			char	   *tmplname = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 4);
			char	   *initopts = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 5);

			if (nspname == NULL || dictname == NULL ||
				tmpl_nsp == NULL || tmplname == NULL)
				continue;

			appendStringInfo(result,
							 "CREATE TEXT SEARCH DICTIONARY %s.%s (TEMPLATE = %s.%s",
							 quote_identifier(nspname),
							 quote_identifier(dictname),
							 quote_identifier(tmpl_nsp),
							 quote_identifier(tmplname));
			if (initopts != NULL && initopts[0] != '\0')
				appendStringInfo(result, ", %s", initopts);
			appendStringInfoString(result, ");\n");
		}
	}

	ret = SPI_execute(
		"SELECT n.nspname, cfg.cfgname, "
		"       pn.nspname AS parser_nsp, p.prsname, "
		"       array_to_string(array("
		"         SELECT 'ALTER TEXT SEARCH CONFIGURATION ' || "
		"                quote_ident(n.nspname) || '.' || quote_ident(cfg.cfgname) || "
		"                ' ADD MAPPING FOR ' || token_type || ' WITH ' || dict_names || ';' "
		"         FROM ("
		"           SELECT t.alias AS token_type, "
		"                  string_agg(quote_ident(dn.nspname) || '.' || quote_ident(d.dictname), "
		"                             ', ' ORDER BY m.mapseqno) AS dict_names "
		"           FROM pg_ts_config_map m "
		"           JOIN pg_ts_dict d ON d.oid = m.mapdict "
		"           JOIN pg_namespace dn ON dn.oid = d.dictnamespace "
		"           JOIN pg_ts_parser pp ON pp.oid = cfg.cfgparser "
		"           JOIN ts_token_type(pp.oid) t ON t.tokid = m.maptokentype "
		"           WHERE m.mapcfg = cfg.oid "
		"           GROUP BY t.alias, m.maptokentype "
		"           ORDER BY m.maptokentype"
		"         ) sub"
		"       ), E'\\n') AS mappings "
		"FROM pg_ts_config cfg "
		"JOIN pg_namespace n ON cfg.cfgnamespace = n.oid "
		"JOIN pg_ts_parser p ON cfg.cfgparser = p.oid "
		"JOIN pg_namespace pn ON p.prsnamespace = pn.oid "
		"WHERE " SKIP_SYSTEM_NSP_SQL " "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_depend d "
		"    WHERE d.objid = cfg.oid AND d.deptype = 'e'"
		"  ) "
		"ORDER BY n.nspname, cfg.cfgname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *nspname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *cfgname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 2);
			char	   *parser_nsp = SPI_getvalue(SPI_tuptable->vals[i],
												  SPI_tuptable->tupdesc, 3);
			char	   *parser_name = SPI_getvalue(SPI_tuptable->vals[i],
												   SPI_tuptable->tupdesc, 4);
			char	   *mappings = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 5);

			if (nspname == NULL || cfgname == NULL ||
				parser_nsp == NULL || parser_name == NULL)
				continue;

			appendStringInfo(result,
							 "CREATE TEXT SEARCH CONFIGURATION %s.%s "
							 "(PARSER = %s.%s);\n",
							 quote_identifier(nspname),
							 quote_identifier(cfgname),
							 quote_identifier(parser_nsp),
							 quote_identifier(parser_name));
			if (mappings != NULL && mappings[0] != '\0')
				appendStringInfo(result, "%s\n", mappings);
		}
	}

	SPI_finish();
	return result;
}

StringInfo
ddl_gen_event_triggers(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT e.evtname, e.evtevent, "
		"       n.nspname AS proc_nsp, p.proname AS proc_name, "
		"       e.evtenabled, e.evttags "
		"FROM pg_event_trigger e "
		"JOIN pg_proc p ON p.oid = e.evtfoid "
		"JOIN pg_namespace n ON n.oid = p.pronamespace "
		"WHERE e.evtname NOT LIKE 'pg_dbbackup_%' "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_depend d "
		"    WHERE d.objid = e.oid AND d.deptype = 'e'"
		"  ) "
		"ORDER BY e.evtname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *evtname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *evtevent = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 2);
			char	   *proc_nsp = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 3);
			char	   *proc_name = SPI_getvalue(SPI_tuptable->vals[i],
												 SPI_tuptable->tupdesc, 4);
			char	   *evtenabled = SPI_getvalue(SPI_tuptable->vals[i],
												  SPI_tuptable->tupdesc, 5);
			char	   *evttags = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 6);

			if (evtname == NULL || evtevent == NULL ||
				proc_nsp == NULL || proc_name == NULL)
				continue;

			appendStringInfo(result,
							 "CREATE EVENT TRIGGER %s ON %s",
							 quote_identifier(evtname), evtevent);
			if (evttags != NULL && evttags[0] != '\0' &&
				strcmp(evttags, "{}") != 0)
				appendStringInfo(result, " WHEN TAG IN (%s)", evttags);
			appendStringInfo(result, " EXECUTE FUNCTION %s.%s();\n",
							 quote_identifier(proc_nsp),
							 quote_identifier(proc_name));

			if (evtenabled != NULL && evtenabled[0] != 'O')
			{
				const char *state = "ENABLE";

				switch (evtenabled[0])
				{
					case 'D': state = "DISABLE"; break;
					case 'R': state = "ENABLE REPLICA"; break;
					case 'A': state = "ENABLE ALWAYS"; break;
				}
				appendStringInfo(result,
								 "ALTER EVENT TRIGGER %s %s;\n",
								 quote_identifier(evtname), state);
			}
		}
	}

	SPI_finish();
	return result;
}

StringInfo
ddl_gen_foreign_data(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();

	/* Foreign data wrappers */
	ret = SPI_execute(
		"SELECT fdw.fdwname, "
		"       hn.nspname AS handler_nsp, hp.proname AS handler_name, "
		"       vn.nspname AS validator_nsp, vp.proname AS validator_name, "
		"       (SELECT string_agg(quote_ident(o.option_name) || ' ' || "
		"                          quote_literal(o.option_value), ', ') "
		"          FROM pg_options_to_table(fdw.fdwoptions) o) AS fdwopts_text "
		"FROM pg_foreign_data_wrapper fdw "
		"LEFT JOIN pg_proc hp ON hp.oid = NULLIF(fdw.fdwhandler, 0) "
		"LEFT JOIN pg_namespace hn ON hn.oid = hp.pronamespace "
		"LEFT JOIN pg_proc vp ON vp.oid = NULLIF(fdw.fdwvalidator, 0) "
		"LEFT JOIN pg_namespace vn ON vn.oid = vp.pronamespace "
		"WHERE NOT EXISTS ("
		"  SELECT 1 FROM pg_depend d "
		"  WHERE d.objid = fdw.oid AND d.deptype = 'e'"
		") "
		"ORDER BY fdw.fdwname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *fdwname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *handler_nsp = SPI_getvalue(SPI_tuptable->vals[i],
												   SPI_tuptable->tupdesc, 2);
			char	   *handler_name = SPI_getvalue(SPI_tuptable->vals[i],
													SPI_tuptable->tupdesc, 3);
			char	   *validator_nsp = SPI_getvalue(SPI_tuptable->vals[i],
													 SPI_tuptable->tupdesc, 4);
			char	   *validator_name = SPI_getvalue(SPI_tuptable->vals[i],
													  SPI_tuptable->tupdesc, 5);
			char	   *fdwopts = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 6);

			if (fdwname == NULL)
				continue;

			appendStringInfo(result, "CREATE FOREIGN DATA WRAPPER %s",
							 quote_identifier(fdwname));
			if (handler_name != NULL && handler_nsp != NULL)
				appendStringInfo(result, " HANDLER %s.%s",
								 quote_identifier(handler_nsp),
								 quote_identifier(handler_name));
			else
				appendStringInfoString(result, " NO HANDLER");
			if (validator_name != NULL && validator_nsp != NULL)
				appendStringInfo(result, " VALIDATOR %s.%s",
								 quote_identifier(validator_nsp),
								 quote_identifier(validator_name));
			else
				appendStringInfoString(result, " NO VALIDATOR");
			if (fdwopts != NULL && fdwopts[0] != '\0')
				appendStringInfo(result, " OPTIONS (%s)", fdwopts);
			appendStringInfoString(result, ";\n");
		}
	}

	/* Foreign servers */
	ret = SPI_execute(
		"SELECT s.srvname, fdw.fdwname, s.srvtype, s.srvversion, "
		"       (SELECT string_agg(quote_ident(o.option_name) || ' ' || "
		"                          quote_literal(o.option_value), ', ') "
		"          FROM pg_options_to_table(s.srvoptions) o) AS srvopts_text "
		"FROM pg_foreign_server s "
		"JOIN pg_foreign_data_wrapper fdw ON s.srvfdw = fdw.oid "
		"WHERE NOT EXISTS ("
		"  SELECT 1 FROM pg_depend d "
		"  WHERE d.objid = s.oid AND d.deptype = 'e'"
		") "
		"ORDER BY s.srvname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *srvname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *fdwname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 2);
			char	   *srvtype = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 3);
			char	   *srvversion = SPI_getvalue(SPI_tuptable->vals[i],
												  SPI_tuptable->tupdesc, 4);
			char	   *srvopts = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 5);

			if (srvname == NULL || fdwname == NULL)
				continue;

			appendStringInfo(result, "CREATE SERVER %s",
							 quote_identifier(srvname));
			if (srvtype != NULL && srvtype[0] != '\0')
				appendStringInfo(result, " TYPE %s",
								 quote_literal_cstr(srvtype));
			if (srvversion != NULL && srvversion[0] != '\0')
				appendStringInfo(result, " VERSION %s",
								 quote_literal_cstr(srvversion));
			appendStringInfo(result, " FOREIGN DATA WRAPPER %s",
							 quote_identifier(fdwname));
			if (srvopts != NULL && srvopts[0] != '\0')
				appendStringInfo(result, " OPTIONS (%s)", srvopts);
			appendStringInfoString(result, ";\n");
		}
	}

	/* User mappings */
	ret = SPI_execute(
		"SELECT CASE WHEN um.umuser = 0 THEN 'PUBLIC' "
		"            ELSE quote_ident(pg_get_userbyid(um.umuser)) END AS rolespec, "
		"       s.srvname, "
		"       (SELECT string_agg(quote_ident(o.option_name) || ' ' || "
		"                          quote_literal(o.option_value), ', ') "
		"          FROM pg_options_to_table(um.umoptions) o) AS umopts_text "
		"FROM pg_user_mapping um "
		"JOIN pg_foreign_server s ON um.umserver = s.oid "
		"WHERE NOT EXISTS ("
		"  SELECT 1 FROM pg_depend d "
		"  WHERE d.objid = um.oid AND d.deptype = 'e'"
		") "
		"ORDER BY s.srvname, rolespec",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *rolespec = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 1);
			char	   *srvname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 2);
			char	   *umopts = SPI_getvalue(SPI_tuptable->vals[i],
											  SPI_tuptable->tupdesc, 3);

			if (rolespec == NULL || srvname == NULL)
				continue;

			appendStringInfo(result,
							 "CREATE USER MAPPING FOR %s SERVER %s",
							 rolespec, quote_identifier(srvname));
			if (umopts != NULL && umopts[0] != '\0')
				appendStringInfo(result, " OPTIONS (%s)", umopts);
			appendStringInfoString(result, ";\n");
		}
	}

	/* Foreign tables */
	ret = SPI_execute(
		"SELECT n.nspname, c.relname, s.srvname, "
		"       (SELECT string_agg(quote_ident(o.option_name) || ' ' || "
		"                          quote_literal(o.option_value), ', ') "
		"          FROM pg_options_to_table(ft.ftoptions) o) AS ftopts_text, "
		"       array_to_string(array("
		"         SELECT quote_ident(a.attname) || ' ' || "
		"                format_type(a.atttypid, a.atttypmod) || "
		"                CASE WHEN a.attnotnull THEN ' NOT NULL' ELSE '' END "
		"         FROM pg_attribute a "
		"         WHERE a.attrelid = c.oid "
		"           AND a.attnum > 0 "
		"           AND NOT a.attisdropped "
		"         ORDER BY a.attnum"
		"       ), ', ') AS cols "
		"FROM pg_foreign_table ft "
		"JOIN pg_class c ON c.oid = ft.ftrelid "
		"JOIN pg_namespace n ON c.relnamespace = n.oid "
		"JOIN pg_foreign_server s ON ft.ftserver = s.oid "
		"WHERE " SKIP_SYSTEM_NSP_SQL " "
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
			char	   *srvname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 3);
			char	   *ftopts = SPI_getvalue(SPI_tuptable->vals[i],
											  SPI_tuptable->tupdesc, 4);
			char	   *cols = SPI_getvalue(SPI_tuptable->vals[i],
											SPI_tuptable->tupdesc, 5);

			if (nspname == NULL || relname == NULL || srvname == NULL)
				continue;

			appendStringInfo(result,
							 "CREATE FOREIGN TABLE IF NOT EXISTS %s.%s (%s) "
							 "SERVER %s",
							 quote_identifier(nspname),
							 quote_identifier(relname),
							 cols ? cols : "",
							 quote_identifier(srvname));
			if (ftopts != NULL && ftopts[0] != '\0')
				appendStringInfo(result, " OPTIONS (%s)", ftopts);
			appendStringInfoString(result, ";\n");
		}
	}

	SPI_finish();
	return result;
}

StringInfo
ddl_gen_publications(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT p.pubname, p.puballtables, p.pubinsert, p.pubupdate, "
		"       p.pubdelete, p.pubtruncate, "
		"       array_to_string(array("
		"         SELECT quote_ident(pn.nspname) || '.' || quote_ident(pc.relname) "
		"         FROM pg_publication_rel pr "
		"         JOIN pg_class pc ON pc.oid = pr.prrelid "
		"         JOIN pg_namespace pn ON pn.oid = pc.relnamespace "
		"         WHERE pr.prpubid = p.oid "
		"         ORDER BY pn.nspname, pc.relname"
		"       ), ', ') AS tables "
		"FROM pg_publication p "
		"WHERE NOT EXISTS ("
		"  SELECT 1 FROM pg_depend d "
		"  WHERE d.objid = p.oid AND d.deptype = 'e'"
		") "
		"ORDER BY p.pubname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *pubname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *all = SPI_getvalue(SPI_tuptable->vals[i],
										   SPI_tuptable->tupdesc, 2);
			char	   *pubins = SPI_getvalue(SPI_tuptable->vals[i],
											  SPI_tuptable->tupdesc, 3);
			char	   *pubupd = SPI_getvalue(SPI_tuptable->vals[i],
											  SPI_tuptable->tupdesc, 4);
			char	   *pubdel = SPI_getvalue(SPI_tuptable->vals[i],
											  SPI_tuptable->tupdesc, 5);
			char	   *pubtrunc = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 6);
			char	   *tables = SPI_getvalue(SPI_tuptable->vals[i],
											  SPI_tuptable->tupdesc, 7);
			StringInfoData ops;

			if (pubname == NULL)
				continue;

			initStringInfo(&ops);
			if (pubins && pubins[0] == 't') appendStringInfoString(&ops, "insert,");
			if (pubupd && pubupd[0] == 't') appendStringInfoString(&ops, "update,");
			if (pubdel && pubdel[0] == 't') appendStringInfoString(&ops, "delete,");
			if (pubtrunc && pubtrunc[0] == 't') appendStringInfoString(&ops, "truncate,");
			if (ops.len > 0)
				ops.data[ops.len - 1] = '\0';

			appendStringInfo(result, "CREATE PUBLICATION %s",
							 quote_identifier(pubname));
			if (all != NULL && all[0] == 't')
				appendStringInfoString(result, " FOR ALL TABLES");
			else if (tables != NULL && tables[0] != '\0')
				appendStringInfo(result, " FOR TABLE %s", tables);
			if (ops.len > 0)
				appendStringInfo(result, " WITH (publish = '%s')", ops.data);
			appendStringInfoString(result, ";\n");

			pfree(ops.data);
		}
	}

	SPI_finish();
	return result;
}

StringInfo
ddl_gen_subscriptions(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;
	Oid			argtypes[1] = {OIDOID};
	Datum		values[1];

	values[0] = ObjectIdGetDatum(db_oid);

	SPI_connect();
	ret = SPI_execute_with_args(
		"SELECT s.subname, s.subconninfo, "
		"       array_to_string(s.subpublications, ', ') AS pubs, "
		"       s.subslotname, s.subsynccommit "
		"FROM pg_subscription s "
		"WHERE s.subdbid = $1",
		1, argtypes, values, NULL, true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *subname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *conninfo = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 2);
			char	   *pubs = SPI_getvalue(SPI_tuptable->vals[i],
											SPI_tuptable->tupdesc, 3);
			char	   *slotname = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 4);

			if (subname == NULL || conninfo == NULL || pubs == NULL)
				continue;

			appendStringInfo(result,
							 "CREATE SUBSCRIPTION %s "
							 "CONNECTION %s "
							 "PUBLICATION %s "
							 "WITH (connect = false, enabled = false, "
							 "      create_slot = false",
							 quote_identifier(subname),
							 quote_literal_cstr(conninfo),
							 pubs);
			if (slotname != NULL && slotname[0] != '\0')
				appendStringInfo(result, ", slot_name = %s",
								 quote_literal_cstr(slotname));
			appendStringInfoString(result, ");\n");
		}
	}

	SPI_finish();
	return result;
}

StringInfo
ddl_gen_collations(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT n.nspname, c.collname, c.collprovider, "
		"       c.collcollate, c.collctype, c.colllocale, "
		"       c.collisdeterministic "
		"FROM pg_collation c "
		"JOIN pg_namespace n ON c.collnamespace = n.oid "
		"WHERE " SKIP_SYSTEM_NSP_SQL " "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_depend d "
		"    WHERE d.objid = c.oid AND d.deptype = 'e'"
		"  ) "
		"ORDER BY n.nspname, c.collname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *nspname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *collname = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 2);
			char	   *provider = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 3);
			char	   *collate = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 4);
			char	   *ctype = SPI_getvalue(SPI_tuptable->vals[i],
											 SPI_tuptable->tupdesc, 5);
			char	   *locale = SPI_getvalue(SPI_tuptable->vals[i],
											  SPI_tuptable->tupdesc, 6);
			char	   *det = SPI_getvalue(SPI_tuptable->vals[i],
										   SPI_tuptable->tupdesc, 7);
			const char *prov = "libc";
			bool		first = true;

			if (nspname == NULL || collname == NULL || provider == NULL)
				continue;

			switch (provider[0])
			{
				case 'i': prov = "icu"; break;
				case 'b': prov = "builtin"; break;
				case 'c': prov = "libc"; break;
				default: prov = "libc"; break;
			}

			appendStringInfo(result,
							 "CREATE COLLATION IF NOT EXISTS %s.%s (",
							 quote_identifier(nspname),
							 quote_identifier(collname));
			appendStringInfo(result, "provider = %s", prov);
			first = false;
			if (locale != NULL)
				appendStringInfo(result, ", locale = %s",
								 quote_literal_cstr(locale));
			else
			{
				if (collate != NULL)
					appendStringInfo(result, ", lc_collate = %s",
									 quote_literal_cstr(collate));
				if (ctype != NULL)
					appendStringInfo(result, ", lc_ctype = %s",
									 quote_literal_cstr(ctype));
			}
			if (det != NULL && det[0] == 'f')
				appendStringInfoString(result, ", deterministic = false");
			(void) first;
			appendStringInfoString(result, ");\n");
		}
	}

	SPI_finish();
	return result;
}

StringInfo
ddl_gen_statistics(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT pg_get_statisticsobjdef(s.oid) AS def "
		"FROM pg_statistic_ext s "
		"JOIN pg_namespace n ON s.stxnamespace = n.oid "
		"WHERE " SKIP_SYSTEM_NSP_SQL " "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_depend d "
		"    WHERE d.objid = s.oid AND d.deptype = 'e'"
		"  ) "
		"ORDER BY n.nspname, s.stxname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *def = SPI_getvalue(SPI_tuptable->vals[i],
										   SPI_tuptable->tupdesc, 1);

			if (def == NULL)
				continue;
			appendStringInfo(result, "%s;\n", def);
		}
	}

	SPI_finish();
	return result;
}

StringInfo
ddl_gen_operators(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT n.nspname, o.oprname, "
		"       CASE WHEN o.oprleft = 0 THEN NULL "
		"            ELSE format_type(o.oprleft, NULL) END AS lefttype, "
		"       CASE WHEN o.oprright = 0 THEN NULL "
		"            ELSE format_type(o.oprright, NULL) END AS righttype, "
		"       pn.nspname, p.proname, "
		"       CASE WHEN o.oprcom = 0 THEN NULL "
		"            ELSE (SELECT oo.oprname FROM pg_operator oo "
		"                  WHERE oo.oid = o.oprcom) END AS commname, "
		"       CASE WHEN o.oprnegate = 0 THEN NULL "
		"            ELSE (SELECT oo.oprname FROM pg_operator oo "
		"                  WHERE oo.oid = o.oprnegate) END AS negname "
		"FROM pg_operator o "
		"JOIN pg_namespace n ON o.oprnamespace = n.oid "
		"JOIN pg_proc p ON p.oid = o.oprcode "
		"JOIN pg_namespace pn ON pn.oid = p.pronamespace "
		"WHERE " SKIP_SYSTEM_NSP_SQL " "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_depend d "
		"    WHERE d.objid = o.oid AND d.deptype = 'e'"
		"  ) "
		"ORDER BY n.nspname, o.oprname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *nspname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *oprname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 2);
			char	   *lefttype = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 3);
			char	   *righttype = SPI_getvalue(SPI_tuptable->vals[i],
												 SPI_tuptable->tupdesc, 4);
			char	   *pn = SPI_getvalue(SPI_tuptable->vals[i],
										  SPI_tuptable->tupdesc, 5);
			char	   *pname = SPI_getvalue(SPI_tuptable->vals[i],
											 SPI_tuptable->tupdesc, 6);
			char	   *commname = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 7);
			char	   *negname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 8);

			if (nspname == NULL || oprname == NULL || pname == NULL)
				continue;

			appendStringInfo(result,
							 "CREATE OPERATOR %s.%s (FUNCTION = %s.%s",
							 quote_identifier(nspname),
							 oprname,
							 quote_identifier(pn),
							 quote_identifier(pname));
			if (lefttype != NULL)
				appendStringInfo(result, ", LEFTARG = %s", lefttype);
			if (righttype != NULL)
				appendStringInfo(result, ", RIGHTARG = %s", righttype);
			if (commname != NULL)
				appendStringInfo(result, ", COMMUTATOR = %s", commname);
			if (negname != NULL)
				appendStringInfo(result, ", NEGATOR = %s", negname);
			appendStringInfoString(result, ");\n");
		}
	}

	SPI_finish();
	return result;
}

StringInfo
ddl_gen_casts(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT format_type(c.castsource, NULL), "
		"       format_type(c.casttarget, NULL), "
		"       fn.nspname, p.proname, c.castcontext "
		"FROM pg_cast c "
		"LEFT JOIN pg_proc p ON p.oid = c.castfunc "
		"LEFT JOIN pg_namespace fn ON fn.oid = p.pronamespace "
		"WHERE c.castfunc <> 0 "
		"  AND fn.nspname IS NOT NULL "
		"  AND fn.nspname NOT LIKE 'pg\\_%' "
		"  AND fn.nspname NOT IN ('information_schema') "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_depend d "
		"    WHERE d.objid = c.oid AND d.deptype = 'e'"
		"  ) "
		"ORDER BY 1, 2",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *src = SPI_getvalue(SPI_tuptable->vals[i],
										   SPI_tuptable->tupdesc, 1);
			char	   *tgt = SPI_getvalue(SPI_tuptable->vals[i],
										   SPI_tuptable->tupdesc, 2);
			char	   *fn_nsp = SPI_getvalue(SPI_tuptable->vals[i],
											  SPI_tuptable->tupdesc, 3);
			char	   *fn_name = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 4);
			char	   *ctx = SPI_getvalue(SPI_tuptable->vals[i],
										   SPI_tuptable->tupdesc, 5);
			const char *kw = "";

			if (src == NULL || tgt == NULL ||
				fn_nsp == NULL || fn_name == NULL || ctx == NULL)
				continue;

			switch (ctx[0])
			{
				case 'a': kw = " AS ASSIGNMENT"; break;
				case 'i': kw = " AS IMPLICIT"; break;
				default: kw = ""; break;
			}

			appendStringInfo(result,
							 "CREATE CAST (%s AS %s) WITH FUNCTION %s.%s(%s)%s;\n",
							 src, tgt,
							 quote_identifier(fn_nsp),
							 quote_identifier(fn_name),
							 src, kw);
		}
	}

	SPI_finish();
	return result;
}

StringInfo
ddl_gen_replica_identity(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT n.nspname, c.relname, c.relreplident, ri.relname "
		"FROM pg_class c "
		"JOIN pg_namespace n ON c.relnamespace = n.oid "
		"LEFT JOIN pg_index i ON i.indrelid = c.oid AND i.indisreplident "
		"LEFT JOIN pg_class ri ON ri.oid = i.indexrelid "
		"WHERE c.relkind IN ('r','p') "
		"  AND " SKIP_SYSTEM_NSP_SQL " "
		"  AND c.relreplident <> 'd' "
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
			char	   *ri = SPI_getvalue(SPI_tuptable->vals[i],
										  SPI_tuptable->tupdesc, 3);
			char	   *idxname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 4);

			if (nspname == NULL || relname == NULL || ri == NULL)
				continue;

			appendStringInfo(result, "ALTER TABLE %s.%s REPLICA IDENTITY ",
							 quote_identifier(nspname),
							 quote_identifier(relname));
			switch (ri[0])
			{
				case 'f':
					appendStringInfoString(result, "FULL");
					break;
				case 'n':
					appendStringInfoString(result, "NOTHING");
					break;
				case 'i':
					if (idxname == NULL)
					{
						resetStringInfo(result);
						continue;
					}
					appendStringInfo(result, "USING INDEX %s",
									 quote_identifier(idxname));
					break;
				default:
					continue;
			}
			appendStringInfoString(result, ";\n");
		}
	}

	SPI_finish();
	return result;
}

StringInfo
ddl_gen_column_attrs(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT n.nspname, c.relname, a.attname, "
		"       a.attstattarget, a.attstorage, "
		"       CASE t.typstorage "
		"            WHEN 'p' THEN 'p' "
		"            WHEN 'e' THEN 'e' "
		"            WHEN 'm' THEN 'm' "
		"            WHEN 'x' THEN 'x' "
		"            ELSE NULL END AS typstorage_default "
		"FROM pg_attribute a "
		"JOIN pg_class c ON c.oid = a.attrelid "
		"JOIN pg_namespace n ON n.oid = c.relnamespace "
		"JOIN pg_type t ON t.oid = a.atttypid "
		"WHERE c.relkind IN ('r','p','m','f') "
		"  AND " SKIP_SYSTEM_NSP_SQL " "
		"  AND a.attnum > 0 AND NOT a.attisdropped "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_depend d "
		"    WHERE d.objid = c.oid AND d.deptype = 'e'"
		"  ) "
		"  AND ((a.attstattarget IS NOT NULL AND a.attstattarget >= 0) "
		"       OR a.attstorage <> t.typstorage) "
		"ORDER BY n.nspname, c.relname, a.attnum",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *nspname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *relname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 2);
			char	   *attname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 3);
			char	   *stat = SPI_getvalue(SPI_tuptable->vals[i],
											SPI_tuptable->tupdesc, 4);
			char	   *storage = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 5);
			char	   *typstor = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 6);

			if (nspname == NULL || relname == NULL || attname == NULL)
				continue;

			if (stat != NULL && stat[0] != '\0' && stat[0] != '-')
			{
				appendStringInfo(result,
								 "ALTER TABLE %s.%s ALTER COLUMN %s "
								 "SET STATISTICS %s;\n",
								 quote_identifier(nspname),
								 quote_identifier(relname),
								 quote_identifier(attname),
								 stat);
			}

			if (storage != NULL && typstor != NULL &&
				storage[0] != typstor[0])
			{
				const char *sname;
				switch (storage[0])
				{
					case 'p': sname = "PLAIN"; break;
					case 'e': sname = "EXTERNAL"; break;
					case 'm': sname = "MAIN"; break;
					case 'x': sname = "EXTENDED"; break;
					default: sname = NULL; break;
				}
				if (sname != NULL)
				{
					appendStringInfo(result,
									 "ALTER TABLE %s.%s ALTER COLUMN %s "
									 "SET STORAGE %s;\n",
									 quote_identifier(nspname),
									 quote_identifier(relname),
									 quote_identifier(attname),
									 sname);
				}
			}
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

	appendStringInfoString(buf, "-- === Collations ===\n");
	part = ddl_gen_collations(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Types ===\n");
	part = ddl_gen_types(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Sequences ===\n");
	part = ddl_gen_sequences(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Foreign data ===\n");
	part = ddl_gen_foreign_data(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Tables ===\n");
	part = ddl_gen_tables(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Inheritance ===\n");
	part = ddl_gen_inheritance(db_oid);
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

	appendStringInfoString(buf, "-- === Statistics ===\n");
	part = ddl_gen_statistics(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Functions ===\n");
	part = ddl_gen_functions(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Operators ===\n");
	part = ddl_gen_operators(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Casts ===\n");
	part = ddl_gen_casts(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Aggregates ===\n");
	part = ddl_gen_aggregates(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Materialized views ===\n");
	part = ddl_gen_materialized_views(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Views ===\n");
	part = ddl_gen_views(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Text search configs ===\n");
	part = ddl_gen_text_search_configs(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Rules ===\n");
	part = ddl_gen_rules(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Triggers ===\n");
	part = ddl_gen_triggers(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Event triggers ===\n");
	part = ddl_gen_event_triggers(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Row security ===\n");
	part = ddl_gen_row_security(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Publications ===\n");
	part = ddl_gen_publications(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Subscriptions ===\n");
	part = ddl_gen_subscriptions(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Replica identity ===\n");
	part = ddl_gen_replica_identity(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Column attributes ===\n");
	part = ddl_gen_column_attrs(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	return buf;
}
