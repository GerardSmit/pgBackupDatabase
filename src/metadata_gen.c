#include "postgres.h"
#include "miscadmin.h"

#include "executor/spi.h"
#include "utils/builtins.h"

#include "metadata_gen.h"

#define SKIP_SYSTEM_NSP_SQL \
	"n.nspname NOT LIKE 'pg\\_%' " \
	"AND n.nspname NOT IN ('information_schema', 'dbbackup', " \
	"'_timescaledb_internal', '_timescaledb_catalog', " \
	"'_timescaledb_config', '_timescaledb_cache', " \
	"'_timescaledb_functions', 'timescaledb_information', " \
	"'timescaledb_experimental')"

StringInfo
metadata_gen_extensions(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT e.extname, e.extversion, n.nspname "
		"FROM pg_extension e "
		"JOIN pg_namespace n ON e.extnamespace = n.oid "
		"WHERE e.extname NOT IN ('plpgsql', 'pg_dbbackup') "
		"ORDER BY e.extname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *extname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *extversion = SPI_getvalue(SPI_tuptable->vals[i],
												  SPI_tuptable->tupdesc, 2);
			char	   *nspname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 3);

			if (extname == NULL)
				continue;

			appendStringInfo(result,
							 "CREATE EXTENSION IF NOT EXISTS %s",
							 quote_identifier(extname));
			if (nspname != NULL)
				appendStringInfo(result, " WITH SCHEMA %s",
								 quote_identifier(nspname));
			if (extversion != NULL)
				appendStringInfo(result, " VERSION %s",
								 quote_literal_cstr(extversion));
			appendStringInfoString(result, ";\n");
		}
	}

	SPI_finish();
	return result;
}

StringInfo
metadata_gen_schemas(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT n.nspname, pg_get_userbyid(n.nspowner) AS owner "
		"FROM pg_namespace n "
		"WHERE " SKIP_SYSTEM_NSP_SQL " "
		"AND NOT EXISTS ("
		"  SELECT 1 FROM pg_depend d "
		"  WHERE d.objid = n.oid "
		"    AND d.classid = 'pg_namespace'::regclass "
		"    AND d.deptype = 'e'"
		") "
		"ORDER BY n.nspname",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *nspname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *owner = SPI_getvalue(SPI_tuptable->vals[i],
											 SPI_tuptable->tupdesc, 2);

			if (nspname == NULL)
				continue;

			appendStringInfo(result,
							 "CREATE SCHEMA IF NOT EXISTS %s;\n",
							 quote_identifier(nspname));
			if (owner != NULL)
				appendStringInfo(result,
								 "ALTER SCHEMA %s OWNER TO %s;\n",
								 quote_identifier(nspname),
								 quote_identifier(owner));
		}
	}

	SPI_finish();
	return result;
}

StringInfo
metadata_gen_db_grants(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	Oid			argtypes[1] = {OIDOID};
	Datum		args[1];
	int			ret;

	args[0] = ObjectIdGetDatum(db_oid);

	SPI_connect();
	ret = SPI_execute_with_args(
		"SELECT d.datname, "
		"       pg_get_userbyid(a.grantee) AS grantee, "
		"       a.privilege_type, "
		"       a.is_grantable "
		"FROM pg_database d, "
		"     aclexplode(d.datacl) a "
		"WHERE d.oid = $1 "
		"  AND a.grantee <> 0",
		1, argtypes, args, NULL, true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *datname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *grantee = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 2);
			char	   *priv = SPI_getvalue(SPI_tuptable->vals[i],
											SPI_tuptable->tupdesc, 3);
			char	   *grantable = SPI_getvalue(SPI_tuptable->vals[i],
												 SPI_tuptable->tupdesc, 4);

			if (datname == NULL || grantee == NULL || priv == NULL)
				continue;

			appendStringInfo(result,
							 "GRANT %s ON DATABASE %s TO %s%s;\n",
							 priv,
							 quote_identifier(datname),
							 quote_identifier(grantee),
							 (grantable && strcmp(grantable, "t") == 0)
								 ? " WITH GRANT OPTION" : "");
		}
	}

	SPI_finish();
	return result;
}

StringInfo
metadata_gen_default_acls(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT pg_get_userbyid(d.defaclrole) AS owner, "
		"       n.nspname, "
		"       d.defaclobjtype, "
		"       pg_get_userbyid(a.grantee) AS grantee, "
		"       a.privilege_type "
		"FROM pg_default_acl d "
		"LEFT JOIN pg_namespace n ON d.defaclnamespace = n.oid, "
		"     aclexplode(d.defaclacl) a "
		"WHERE (n.nspname IS NULL OR (" SKIP_SYSTEM_NSP_SQL ")) "
		"  AND a.grantee <> 0 "
		"ORDER BY n.nspname, d.defaclobjtype",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *owner = SPI_getvalue(SPI_tuptable->vals[i],
											 SPI_tuptable->tupdesc, 1);
			char	   *nspname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 2);
			char	   *objtype = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 3);
			char	   *grantee = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 4);
			char	   *priv = SPI_getvalue(SPI_tuptable->vals[i],
											SPI_tuptable->tupdesc, 5);
			const char *target = "TABLES";

			if (owner == NULL || objtype == NULL || grantee == NULL || priv == NULL)
				continue;

			switch (objtype[0])
			{
				case 'r': target = "TABLES"; break;
				case 'S': target = "SEQUENCES"; break;
				case 'f': target = "FUNCTIONS"; break;
				case 'T': target = "TYPES"; break;
				case 'n': target = "SCHEMAS"; break;
				default: continue;
			}

			appendStringInfoString(result, "ALTER DEFAULT PRIVILEGES");
			appendStringInfo(result, " FOR ROLE %s", quote_identifier(owner));
			if (nspname != NULL)
				appendStringInfo(result, " IN SCHEMA %s",
								 quote_identifier(nspname));
			appendStringInfo(result, " GRANT %s ON %s TO %s;\n",
							 priv, target, quote_identifier(grantee));
		}
	}

	SPI_finish();
	return result;
}

StringInfo
metadata_gen_object_grants(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT n.nspname, c.relname, c.relkind, "
		"       pg_get_userbyid(a.grantee) AS grantee, "
		"       a.privilege_type, a.is_grantable "
		"FROM pg_class c "
		"JOIN pg_namespace n ON c.relnamespace = n.oid, "
		"     aclexplode(c.relacl) a "
		"WHERE " SKIP_SYSTEM_NSP_SQL " "
		"  AND c.relkind IN ('r', 'v', 'm', 'S', 'f', 'p') "
		"  AND a.grantee <> 0 "
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
			char	   *relkind = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 3);
			char	   *grantee = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 4);
			char	   *priv = SPI_getvalue(SPI_tuptable->vals[i],
											SPI_tuptable->tupdesc, 5);
			char	   *grantable = SPI_getvalue(SPI_tuptable->vals[i],
												 SPI_tuptable->tupdesc, 6);
			const char *kind_label = "TABLE";

			if (nspname == NULL || relname == NULL || relkind == NULL ||
				grantee == NULL || priv == NULL)
				continue;

			if (relkind[0] == 'S')
				kind_label = "SEQUENCE";
			else
				kind_label = "TABLE";

			appendStringInfo(result,
							 "GRANT %s ON %s %s.%s TO %s%s;\n",
							 priv,
							 kind_label,
							 quote_identifier(nspname),
							 quote_identifier(relname),
							 quote_identifier(grantee),
							 (grantable && strcmp(grantable, "t") == 0)
								 ? " WITH GRANT OPTION" : "");
		}
	}

	SPI_finish();
	return result;
}

StringInfo
metadata_gen_comments(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT n.nspname, c.relname, c.relkind, d.description "
		"FROM pg_description d "
		"JOIN pg_class c ON d.objoid = c.oid AND d.classoid = 'pg_class'::regclass "
		"JOIN pg_namespace n ON c.relnamespace = n.oid "
		"WHERE " SKIP_SYSTEM_NSP_SQL " "
		"  AND d.objsubid = 0 "
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
			char	   *relkind = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 3);
			char	   *descr = SPI_getvalue(SPI_tuptable->vals[i],
											 SPI_tuptable->tupdesc, 4);
			const char *kind_label = "TABLE";

			if (nspname == NULL || relname == NULL || relkind == NULL ||
				descr == NULL)
				continue;

			switch (relkind[0])
			{
				case 'r': kind_label = "TABLE"; break;
				case 'v': kind_label = "VIEW"; break;
				case 'm': kind_label = "MATERIALIZED VIEW"; break;
				case 'S': kind_label = "SEQUENCE"; break;
				case 'f': kind_label = "FOREIGN TABLE"; break;
				case 'p': kind_label = "TABLE"; break;
				default: continue;
			}

			appendStringInfo(result,
							 "COMMENT ON %s %s.%s IS %s;\n",
							 kind_label,
							 quote_identifier(nspname),
							 quote_identifier(relname),
							 quote_literal_cstr(descr));
		}
	}

	SPI_finish();
	return result;
}

StringInfo
metadata_gen_db_settings(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	Oid			argtypes[1] = {OIDOID};
	Datum		args[1];
	int			ret;

	args[0] = ObjectIdGetDatum(db_oid);

	SPI_connect();
	ret = SPI_execute_with_args(
		"SELECT d.datname, "
		"       CASE WHEN s.setrole = 0 THEN NULL "
		"            ELSE pg_get_userbyid(s.setrole) END AS rolename, "
		"       unnest(s.setconfig) AS setting "
		"FROM pg_db_role_setting s "
		"JOIN pg_database d ON s.setdatabase = d.oid "
		"WHERE s.setdatabase = $1",
		1, argtypes, args, NULL, true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *datname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *rolename = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 2);
			char	   *setting = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 3);
			char	   *eq;

			if (datname == NULL || setting == NULL)
				continue;

			eq = strchr(setting, '=');
			if (eq == NULL)
				continue;
			*eq = '\0';

			appendStringInfo(result, "ALTER DATABASE %s",
							 quote_identifier(datname));
			if (rolename != NULL)
				appendStringInfo(result, " IN ROLE %s",
								 quote_identifier(rolename));
			appendStringInfo(result, " SET %s = %s;\n",
							 setting, quote_literal_cstr(eq + 1));
		}
	}

	SPI_finish();
	return result;
}

/*
 * Detect TimescaleDB objects in the source DB and emit the SQL needed
 * to reconstruct them on the restore target after the plain user-table
 * data has been COPY'd back in:
 *
 *   1. create_hypertable(... migrate_data => true) for each hypertable,
 *      so that the freshly-initialised _timescaledb_catalog (cleared by
 *      CREATE EXTENSION on the target) is repopulated and the COPY'd
 *      rows are routed into chunks.
 *   2. ALTER TABLE ... SET (timescaledb.compress, ...) +
 *      compress_chunk(...) for hypertables that had compression
 *      enabled on the source.
 *   3. CREATE MATERIALIZED VIEW ... WITH (timescaledb.continuous) AS ...
 *      + refresh_continuous_aggregate(...) for each CAGG.
 *
 * Only the single time-dimension hypertable shape is handled — multi-
 * dimensional hypertables would resurface as orphaned chunks.
 */
StringInfo
metadata_gen_timescaledb(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;
	bool		has_ts = false;
	bool		has_compression_settings = false;

	(void) db_oid;

	SPI_connect();

	ret = SPI_execute(
		"SELECT 1 FROM pg_extension WHERE extname = 'timescaledb'",
		true, 0);
	if (ret == SPI_OK_SELECT && SPI_processed > 0)
		has_ts = true;

	if (!has_ts)
	{
		SPI_finish();
		return result;
	}

	/* hypertables + time dimension */
	ret = SPI_execute(
		"SELECT h.schema_name, h.table_name, d.column_name, d.column_type, "
		"       d.interval_length "
		"FROM _timescaledb_catalog.hypertable h "
		"JOIN _timescaledb_catalog.dimension d ON d.hypertable_id = h.id "
		"WHERE d.interval_length IS NOT NULL "
		"  AND h.schema_name NOT IN ('_timescaledb_internal', "
		"                            '_timescaledb_catalog') "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM _timescaledb_catalog.continuous_agg ca "
		"    WHERE ca.mat_hypertable_id = h.id"
		"  ) "
		"ORDER BY h.id, d.id",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		uint64		i;

		for (i = 0; i < SPI_processed; i++)
		{
			char	   *schema = SPI_getvalue(SPI_tuptable->vals[i],
											  SPI_tuptable->tupdesc, 1);
			char	   *table = SPI_getvalue(SPI_tuptable->vals[i],
											 SPI_tuptable->tupdesc, 2);
			char	   *colname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 3);
			char	   *coltype = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 4);
			char	   *ivl = SPI_getvalue(SPI_tuptable->vals[i],
										   SPI_tuptable->tupdesc, 5);
			StringInfoData qualified;
			StringInfoData chunk_arg;

			if (schema == NULL || table == NULL || colname == NULL ||
				coltype == NULL || ivl == NULL)
				continue;

			initStringInfo(&qualified);
			appendStringInfo(&qualified, "%s.%s",
							 quote_identifier(schema),
							 quote_identifier(table));

			initStringInfo(&chunk_arg);
			if (coltype != NULL &&
				(strstr(coltype, "timestamp") != NULL ||
				 strstr(coltype, "date") != NULL))
			{
				appendStringInfo(&chunk_arg,
								 "interval '%s microseconds'", ivl);
			}
			else
			{
				appendStringInfoString(&chunk_arg, ivl);
			}

			appendStringInfo(result,
							 "SELECT public.create_hypertable("
							 "%s, %s, "
							 "chunk_time_interval => %s, "
							 "if_not_exists => true, "
							 "migrate_data => true);\n",
							 quote_literal_cstr(qualified.data),
							 quote_literal_cstr(colname),
							 chunk_arg.data);

			pfree(qualified.data);
			pfree(chunk_arg.data);
		}
	}

	/*
	 * Compression settings. The schema of this table varies across TS
	 * versions:
	 *   * TS <2.18 has hypertable_id + the *_orderby / *_segmentby cols.
	 *   * TS >=2.18 keys by relid (pg_class oid) and exposes the same
	 *     columns under different names (orderby, segmentby).
	 * We probe pg_attribute for the relid form first and fall back.
	 */
	ret = SPI_execute(
		"SELECT 1 FROM pg_class c "
		"JOIN pg_namespace n ON c.relnamespace = n.oid "
		"WHERE n.nspname = '_timescaledb_catalog' "
		"  AND c.relname = 'compression_settings'",
		true, 0);
	if (ret == SPI_OK_SELECT && SPI_processed > 0)
		has_compression_settings = true;

	if (has_compression_settings)
	{
		bool		has_relid = false;

		ret = SPI_execute(
			"SELECT 1 FROM pg_attribute a "
			"JOIN pg_class c ON a.attrelid = c.oid "
			"JOIN pg_namespace n ON c.relnamespace = n.oid "
			"WHERE n.nspname = '_timescaledb_catalog' "
			"  AND c.relname = 'compression_settings' "
			"  AND a.attname = 'relid' "
			"  AND NOT a.attisdropped",
			true, 0);
		if (ret == SPI_OK_SELECT && SPI_processed > 0)
			has_relid = true;

		if (has_relid)
		{
			/*
			 * TS >=2.18 form: compression_settings.relid is a pg_class
			 * oid that points at the hypertable's parent class (when the
			 * settings row represents the hypertable rather than a per-
			 * chunk override). The same table also stores per-chunk
			 * settings; we filter to the hypertable parent only.
			 */
			ret = SPI_execute(
				"SELECT n.nspname, c.relname, "
				"       array_to_string(cs.segmentby, ',') AS seg, "
				"       array_to_string(cs.orderby, ',') AS ord, "
				"       array_to_string(cs.orderby_desc, ',') AS ord_desc "
				"FROM _timescaledb_catalog.compression_settings cs "
				"JOIN pg_class c ON cs.relid = c.oid "
				"JOIN pg_namespace n ON c.relnamespace = n.oid "
				"JOIN _timescaledb_catalog.hypertable h "
				"     ON h.schema_name = n.nspname "
				"    AND h.table_name = c.relname "
				"WHERE n.nspname NOT IN ('_timescaledb_internal', "
				"                        '_timescaledb_catalog')",
				true, 0);
		}
		else
		{
			ret = SPI_execute(
				"SELECT h.schema_name, h.table_name, "
				"       cs.segmentby, cs.orderby, NULL::text AS ord_desc "
				"FROM _timescaledb_catalog.compression_settings cs "
				"JOIN _timescaledb_catalog.hypertable h "
				"     ON h.id = cs.hypertable_id "
				"WHERE h.schema_name NOT IN ('_timescaledb_internal', "
				"                            '_timescaledb_catalog')",
				true, 0);
		}

		if (ret == SPI_OK_SELECT)
		{
			uint64		i;

			for (i = 0; i < SPI_processed; i++)
			{
				char	   *schema = SPI_getvalue(SPI_tuptable->vals[i],
												  SPI_tuptable->tupdesc, 1);
				char	   *table = SPI_getvalue(SPI_tuptable->vals[i],
												 SPI_tuptable->tupdesc, 2);
				char	   *seg = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 3);
				char	   *ord = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 4);
				StringInfoData qualified;
				StringInfoData opts;
				bool		first = true;

				if (schema == NULL || table == NULL)
					continue;

				initStringInfo(&qualified);
				appendStringInfo(&qualified, "%s.%s",
								 quote_identifier(schema),
								 quote_identifier(table));

				initStringInfo(&opts);
				appendStringInfoString(&opts, "timescaledb.compress");
				first = false;
				if (seg != NULL && seg[0] != '\0')
				{
					if (!first)
						appendStringInfoString(&opts, ", ");
					appendStringInfo(&opts,
									 "timescaledb.compress_segmentby = %s",
									 quote_literal_cstr(seg));
					first = false;
				}
				if (ord != NULL && ord[0] != '\0')
				{
					if (!first)
						appendStringInfoString(&opts, ", ");
					appendStringInfo(&opts,
									 "timescaledb.compress_orderby = %s",
									 quote_literal_cstr(ord));
				}

				appendStringInfo(result, "ALTER TABLE %s SET (%s);\n",
								 qualified.data, opts.data);
				appendStringInfo(result,
								 "SELECT public.compress_chunk(c) "
								 "FROM public.show_chunks(%s) c;\n",
								 quote_literal_cstr(qualified.data));

				pfree(qualified.data);
				pfree(opts.data);
			}
		}
	}

	/*
	 * Continuous aggregates. We recover the original SELECT body from
	 * pg_views (the user-visible view IS a regular view that proxies
	 * the materialization hypertable). Emit CREATE MATERIALIZED VIEW
	 * WITH (timescaledb.continuous) ... WITH NO DATA, then refresh.
	 */
	ret = SPI_execute(
		"SELECT 1 FROM pg_class c "
		"JOIN pg_namespace n ON c.relnamespace = n.oid "
		"WHERE n.nspname = '_timescaledb_catalog' "
		"  AND c.relname = 'continuous_agg'",
		true, 0);
	if (ret == SPI_OK_SELECT && SPI_processed > 0)
	{
		ret = SPI_execute(
			"SELECT ca.user_view_schema, ca.user_view_name, "
			"       pg_get_viewdef(format('%I.%I', "
			"                             ca.direct_view_schema, "
			"                             ca.direct_view_name)::regclass, true) "
			"FROM _timescaledb_catalog.continuous_agg ca "
			"ORDER BY ca.mat_hypertable_id",
			true, 0);

		if (ret == SPI_OK_SELECT)
		{
			uint64		i;

			for (i = 0; i < SPI_processed; i++)
			{
				char	   *schema = SPI_getvalue(SPI_tuptable->vals[i],
												  SPI_tuptable->tupdesc, 1);
				char	   *view = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 2);
				char	   *def = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 3);
				size_t		deflen;

				if (schema == NULL || view == NULL || def == NULL)
					continue;

				/*
				 * pg_get_viewdef returns the SELECT with a trailing
				 * semicolon and newline; strip them so we can embed it
				 * inside CREATE MATERIALIZED VIEW ... AS ... WITH NO DATA.
				 */
				deflen = strlen(def);
				while (deflen > 0 && (def[deflen - 1] == ';' ||
									  def[deflen - 1] == '\n' ||
									  def[deflen - 1] == '\r' ||
									  def[deflen - 1] == ' ' ||
									  def[deflen - 1] == '\t'))
				{
					def[deflen - 1] = '\0';
					deflen--;
				}

				appendStringInfo(result,
								 "CREATE MATERIALIZED VIEW IF NOT EXISTS "
								 "%s.%s "
								 "WITH (timescaledb.continuous) AS\n%s\n"
								 "WITH NO DATA;\n",
								 quote_identifier(schema),
								 quote_identifier(view),
								 def);
				appendStringInfo(result,
								 "CALL public.refresh_continuous_aggregate("
								 "%s, NULL, NULL);\n",
								 quote_literal_cstr(
									 psprintf("%s.%s",
											  quote_identifier(schema),
											  quote_identifier(view))));
			}
		}
	}

	SPI_finish();
	return result;
}

StringInfo
metadata_gen_all(Oid db_oid)
{
	StringInfo	buf = makeStringInfo();
	StringInfo	part;

	appendStringInfoString(buf, "-- === Extensions ===\n");
	part = metadata_gen_extensions(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Schemas ===\n");
	part = metadata_gen_schemas(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Database grants ===\n");
	part = metadata_gen_db_grants(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Default ACLs ===\n");
	part = metadata_gen_default_acls(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Object grants ===\n");
	part = metadata_gen_object_grants(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Comments ===\n");
	part = metadata_gen_comments(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Database settings ===\n");
	part = metadata_gen_db_settings(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === TimescaleDB hypertables ===\n");
	part = metadata_gen_timescaledb(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	return buf;
}
