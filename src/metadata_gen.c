#include "postgres.h"
#include "miscadmin.h"

#include "executor/spi.h"
#include "utils/builtins.h"
#include "utils/memutils.h"

#include "metadata_gen.h"

#define SKIP_SYSTEM_NSP_SQL \
	"n.nspname NOT LIKE 'pg\\_%' " \
	"AND n.nspname NOT IN ('information_schema', 'dbbackup', " \
	"'_timescaledb_internal', '_timescaledb_catalog', " \
	"'_timescaledb_config', '_timescaledb_cache', " \
	"'_timescaledb_functions', 'timescaledb_information', " \
	"'timescaledb_experimental')"

typedef struct SequenceStateTarget
{
	char	   *nspname;
	char	   *relname;
	char	   *query;
} SequenceStateTarget;

static bool
looks_integer_literal(const char *s)
{
	const char *p;

	if (s == NULL || *s == '\0')
		return false;
	p = s;
	if (*p == '-')
		p++;
	if (*p == '\0')
		return false;
	for (; *p; p++)
	{
		if (*p < '0' || *p > '9')
			return false;
	}
	return true;
}

static void
append_timescale_interval_arg(StringInfo out, const char *value)
{
	if (value == NULL || value[0] == '\0')
	{
		appendStringInfoString(out, "NULL");
		return;
	}

	if (looks_integer_literal(value))
		appendStringInfoString(out, value);
	else
		appendStringInfo(out, "INTERVAL %s", quote_literal_cstr(value));
}

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
metadata_gen_roles(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	Oid			argtypes[1] = {OIDOID};
	Datum		args[1];
	int			ret;

	args[0] = ObjectIdGetDatum(db_oid);

	SPI_connect();
	ret = SPI_execute_with_args(
		"WITH role_names(role_name) AS ("
		"  SELECT pg_get_userbyid(d.datdba) "
		"  FROM pg_database d "
		"  WHERE d.oid = $1 "
		"UNION "
		"  SELECT pg_get_userbyid(n.nspowner) "
		"  FROM pg_namespace n "
		"  WHERE " SKIP_SYSTEM_NSP_SQL " "
		"UNION "
		"  SELECT pg_get_userbyid(c.relowner) "
		"  FROM pg_class c "
		"  JOIN pg_namespace n ON c.relnamespace = n.oid "
		"  WHERE " SKIP_SYSTEM_NSP_SQL " "
		"    AND c.relkind IN ('r', 'v', 'm', 'S', 'f', 'p') "
		"    AND NOT EXISTS ("
		"      SELECT 1 FROM pg_depend d "
		"      WHERE d.objid = c.oid AND d.deptype = 'e'"
		"    ) "
		"UNION "
		"  SELECT pg_get_userbyid(a.grantee) "
		"  FROM pg_class c "
		"  JOIN pg_namespace n ON c.relnamespace = n.oid, "
		"       aclexplode(c.relacl) a "
		"  WHERE " SKIP_SYSTEM_NSP_SQL " "
		"    AND c.relkind IN ('r', 'v', 'm', 'S', 'f', 'p') "
		"    AND a.grantee <> 0 "
		"    AND NOT EXISTS ("
		"      SELECT 1 FROM pg_depend d "
		"      WHERE d.objid = c.oid AND d.deptype = 'e'"
		"    ) "
		"UNION "
		"  SELECT pg_get_userbyid(d.defaclrole) "
		"  FROM pg_default_acl d "
		"UNION "
		"  SELECT pg_get_userbyid(a.grantee) "
		"  FROM pg_default_acl d, aclexplode(d.defaclacl) a "
		"  WHERE a.grantee <> 0 "
		"UNION "
		"  SELECT pg_get_userbyid(a.grantee) "
		"  FROM pg_database d, aclexplode(d.datacl) a "
		"  WHERE d.oid = $1 AND a.grantee <> 0 "
		"UNION "
		"  SELECT pg_get_userbyid(s.setrole) "
		"  FROM pg_db_role_setting s "
		"  WHERE s.setdatabase = $1 AND s.setrole <> 0 "
	"UNION "
	"  SELECT pg_get_userbyid(role_oid) "
	"  FROM pg_policy pol, unnest(pol.polroles) AS role_oid "
	"  WHERE role_oid <> 0 "
	"UNION "
	"  SELECT pg_get_userbyid(m.lomowner) "
	"  FROM pg_largeobject_metadata m "
		"UNION "
		"  SELECT pg_get_userbyid(a.grantee) "
		"  FROM pg_largeobject_metadata m, aclexplode(m.lomacl) a "
		"  WHERE a.grantee <> 0"
	") "
	"SELECT DISTINCT role_name "
		"FROM role_names "
		"WHERE role_name IS NOT NULL "
		"ORDER BY role_name",
		1, argtypes, args, NULL, true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *role = SPI_getvalue(SPI_tuptable->vals[i],
											SPI_tuptable->tupdesc, 1);

			if (role == NULL)
				continue;

			appendStringInfo(result,
							 "DO $pg_dbbackup_role$\n"
							 "BEGIN\n"
							 "  IF NOT EXISTS ("
							 "SELECT 1 FROM pg_catalog.pg_roles "
							 "WHERE rolname = %s) THEN\n"
							 "    RAISE WARNING 'pg_dbbackup: role \"%%\" did not exist on restore target; creating NOLOGIN placeholder role without passwords or memberships', %s;\n"
							 "    CREATE ROLE %s NOLOGIN;\n"
							 "  END IF;\n"
							 "END\n"
							 "$pg_dbbackup_role$;\n",
							 quote_literal_cstr(role),
							 quote_literal_cstr(role),
							 quote_identifier(role));
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
metadata_gen_object_owners(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT n.nspname, c.relname, c.relkind, "
		"       pg_get_userbyid(c.relowner) AS owner "
		"FROM pg_class c "
		"JOIN pg_namespace n ON c.relnamespace = n.oid "
		"WHERE " SKIP_SYSTEM_NSP_SQL " "
		"  AND c.relkind IN ('r', 'v', 'm', 'S', 'f', 'p') "
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
			char	   *owner = SPI_getvalue(SPI_tuptable->vals[i],
											 SPI_tuptable->tupdesc, 4);
			const char *kind_label = "TABLE";

			if (nspname == NULL || relname == NULL ||
				relkind == NULL || owner == NULL)
				continue;

			switch (relkind[0])
			{
				case 'S': kind_label = "SEQUENCE"; break;
				case 'm': kind_label = "MATERIALIZED VIEW"; break;
				default: kind_label = "TABLE"; break;
			}

			appendStringInfo(result,
							 "ALTER %s %s.%s OWNER TO %s;\n",
							 kind_label,
							 quote_identifier(nspname),
							 quote_identifier(relname),
							 quote_identifier(owner));
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

static void
append_timescaledb_policies(StringInfo result)
{
	int			ret;

	ret = SPI_execute(
		"SELECT 1 FROM pg_class c "
		"JOIN pg_namespace n ON c.relnamespace = n.oid "
		"WHERE n.nspname = '_timescaledb_config' "
		"  AND c.relname = 'bgw_job'",
		true, 0);
	if (ret != SPI_OK_SELECT || SPI_processed == 0)
		return;

	ret = SPI_execute(
		"SELECT j.proc_name::text, j.schedule_interval::text, "
		"       h.schema_name, h.table_name, "
		"       ca.user_view_schema, ca.user_view_name, "
		"       j.config->>'drop_after', "
		"       j.config->>'compress_after', "
		"       j.config->>'start_offset', "
		"       j.config->>'end_offset' "
		"FROM _timescaledb_config.bgw_job j "
		"LEFT JOIN _timescaledb_catalog.hypertable h "
		"  ON h.id = j.hypertable_id "
		"LEFT JOIN _timescaledb_catalog.continuous_agg ca "
		"  ON ca.mat_hypertable_id = j.hypertable_id "
		"WHERE j.proc_name IN ("
		"  'policy_retention', "
		"  'policy_compression', "
		"  'policy_refresh_continuous_aggregate') "
		"ORDER BY j.id",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		uint64		i;

		for (i = 0; i < SPI_processed; i++)
		{
			char	   *proc = SPI_getvalue(SPI_tuptable->vals[i],
											SPI_tuptable->tupdesc, 1);
			char	   *schedule = SPI_getvalue(SPI_tuptable->vals[i],
												SPI_tuptable->tupdesc, 2);
			char	   *schema = SPI_getvalue(SPI_tuptable->vals[i],
											  SPI_tuptable->tupdesc, 3);
			char	   *table = SPI_getvalue(SPI_tuptable->vals[i],
											 SPI_tuptable->tupdesc, 4);
			char	   *view_schema = SPI_getvalue(SPI_tuptable->vals[i],
												   SPI_tuptable->tupdesc, 5);
			char	   *view_name = SPI_getvalue(SPI_tuptable->vals[i],
												 SPI_tuptable->tupdesc, 6);
			char	   *drop_after = SPI_getvalue(SPI_tuptable->vals[i],
												  SPI_tuptable->tupdesc, 7);
			char	   *compress_after = SPI_getvalue(SPI_tuptable->vals[i],
													  SPI_tuptable->tupdesc, 8);
			char	   *start_offset = SPI_getvalue(SPI_tuptable->vals[i],
													SPI_tuptable->tupdesc, 9);
			char	   *end_offset = SPI_getvalue(SPI_tuptable->vals[i],
												  SPI_tuptable->tupdesc, 10);
			StringInfoData qualified;

			if (proc == NULL)
				continue;

			initStringInfo(&qualified);
			if (strcmp(proc, "policy_refresh_continuous_aggregate") == 0)
			{
				if (view_schema == NULL || view_name == NULL)
				{
					pfree(qualified.data);
					continue;
				}
				appendStringInfo(&qualified, "%s.%s",
								 quote_identifier(view_schema),
								 quote_identifier(view_name));
				appendStringInfo(result,
								 "SELECT public.add_continuous_aggregate_policy("
								 "%s, start_offset => ",
								 quote_literal_cstr(qualified.data));
				append_timescale_interval_arg(result, start_offset);
				appendStringInfoString(result, ", end_offset => ");
				append_timescale_interval_arg(result, end_offset);
				if (schedule != NULL)
				{
					appendStringInfo(result,
									 ", schedule_interval => INTERVAL %s",
									 quote_literal_cstr(schedule));
				}
				appendStringInfoString(result,
									   ", if_not_exists => true);\n");
			}
			else
			{
				if (schema == NULL || table == NULL)
				{
					pfree(qualified.data);
					continue;
				}
				appendStringInfo(&qualified, "%s.%s",
								 quote_identifier(schema),
								 quote_identifier(table));

				if (strcmp(proc, "policy_retention") == 0)
				{
					appendStringInfo(result,
									 "SELECT public.add_retention_policy("
									 "%s, drop_after => ",
									 quote_literal_cstr(qualified.data));
					append_timescale_interval_arg(result, drop_after);
					if (schedule != NULL)
					{
						appendStringInfo(result,
										 ", schedule_interval => INTERVAL %s",
										 quote_literal_cstr(schedule));
					}
					appendStringInfoString(result,
										   ", if_not_exists => true);\n");
				}
				else if (strcmp(proc, "policy_compression") == 0)
				{
					appendStringInfo(result,
									 "SELECT public.add_compression_policy("
									 "%s, compress_after => ",
									 quote_literal_cstr(qualified.data));
					append_timescale_interval_arg(result, compress_after);
					if (schedule != NULL)
					{
						appendStringInfo(result,
										 ", schedule_interval => INTERVAL %s",
										 quote_literal_cstr(schedule));
					}
					appendStringInfoString(result,
										   ", if_not_exists => true);\n");
				}
			}

			pfree(qualified.data);
		}
	}
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
		"SELECT DISTINCT ON (h.id) "
		"       h.schema_name, h.table_name, d.column_name, d.column_type, "
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

	/* additional range/hash dimensions */
	ret = SPI_execute(
		"WITH first_dim AS ("
		"  SELECT hypertable_id, min(id) AS first_id "
		"  FROM _timescaledb_catalog.dimension "
		"  GROUP BY hypertable_id"
		") "
		"SELECT h.schema_name, h.table_name, d.column_name, d.column_type, "
		"       d.interval_length, d.num_slices "
		"FROM _timescaledb_catalog.hypertable h "
		"JOIN first_dim f ON f.hypertable_id = h.id "
		"JOIN _timescaledb_catalog.dimension d "
		"  ON d.hypertable_id = h.id AND d.id <> f.first_id "
		"WHERE h.schema_name NOT IN ('_timescaledb_internal', "
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
			char	   *slices = SPI_getvalue(SPI_tuptable->vals[i],
											  SPI_tuptable->tupdesc, 6);
			StringInfoData qualified;

			if (schema == NULL || table == NULL || colname == NULL)
				continue;

			initStringInfo(&qualified);
			appendStringInfo(&qualified, "%s.%s",
							 quote_identifier(schema),
							 quote_identifier(table));

			if (ivl != NULL)
			{
				StringInfoData chunk_arg;

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
								 "SELECT public.add_dimension("
								 "%s, public.by_range(%s, %s), "
								 "if_not_exists => true);\n",
								 quote_literal_cstr(qualified.data),
								 quote_literal_cstr(colname),
								 chunk_arg.data);
				pfree(chunk_arg.data);
			}
			else if (slices != NULL)
			{
				appendStringInfo(result,
								 "SELECT public.add_dimension("
								 "%s, public.by_hash(%s, %s), "
								 "if_not_exists => true);\n",
								 quote_literal_cstr(qualified.data),
								 quote_literal_cstr(colname),
								 slices);
			}
			else
			{
				SPI_finish();
				ereport(ERROR,
						(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
						 errmsg("unsupported TimescaleDB dimension on %s",
								qualified.data),
						 errdetail("Dimension column \"%s\" has neither interval_length nor num_slices.",
								   colname)));
			}

			pfree(qualified.data);
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

	append_timescaledb_policies(result);

	SPI_finish();
	return result;
}

StringInfo
metadata_gen_sequence_values(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	SequenceStateTarget *targets = NULL;
	MemoryContext caller_ctx = CurrentMemoryContext;
	uint64		target_count = 0;
	int			ret;

	(void) db_oid;

	SPI_connect();
	ret = SPI_execute(
		"SELECT n.nspname, c.relname "
		"FROM pg_class c "
		"JOIN pg_namespace n ON c.relnamespace = n.oid "
		"WHERE c.relkind = 'S' "
		"  AND " SKIP_SYSTEM_NSP_SQL " "
		"  AND NOT EXISTS ("
		"    SELECT 1 FROM pg_depend d "
		"    WHERE d.objid = c.oid AND d.deptype = 'e'"
		"  ) "
		"ORDER BY n.nspname, c.relname",
		true, 0);

	if (ret == SPI_OK_SELECT && SPI_processed > 0)
	{
		MemoryContext oldctx;

		target_count = SPI_processed;
		oldctx = MemoryContextSwitchTo(caller_ctx);
		targets = palloc0(sizeof(SequenceStateTarget) * target_count);

		for (uint64 i = 0; i < target_count; i++)
		{
			char	   *nspname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 1);
			char	   *relname = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 2);

			if (nspname == NULL || relname == NULL)
				continue;

			targets[i].nspname = pstrdup(nspname);
			targets[i].relname = pstrdup(relname);
			targets[i].query = psprintf(
				"SELECT last_value::text, is_called::text FROM %s.%s",
				quote_identifier(nspname),
				quote_identifier(relname));
		}
		MemoryContextSwitchTo(oldctx);
	}
	SPI_finish();

	if (targets == NULL)
		return result;

	SPI_connect();
	for (uint64 i = 0; i < target_count; i++)
	{
		char	   *last_value;
		char	   *is_called;
		char	   *qualified;

		if (targets[i].query == NULL)
			continue;

		ret = SPI_execute(targets[i].query, true, 1);
		if (ret != SPI_OK_SELECT || SPI_processed != 1)
			continue;

		last_value = SPI_getvalue(SPI_tuptable->vals[0],
								  SPI_tuptable->tupdesc, 1);
		is_called = SPI_getvalue(SPI_tuptable->vals[0],
								  SPI_tuptable->tupdesc, 2);
		if (last_value == NULL || is_called == NULL)
			continue;

		qualified = quote_qualified_identifier(targets[i].nspname,
											   targets[i].relname);
		appendStringInfo(result,
						 "SELECT pg_catalog.setval(%s::regclass, %s, %s);\n",
						 quote_literal_cstr(qualified),
						 last_value,
						 (is_called[0] == 't') ? "true" : "false");
	}
	SPI_finish();

	return result;
}

StringInfo
metadata_gen_large_objects(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	StringInfoData oid_array;
	int			ret;

	(void) db_oid;

	initStringInfo(&oid_array);
	appendStringInfoString(&oid_array, "ARRAY[");

	SPI_connect();
	ret = SPI_execute(
		"SELECT oid::text, pg_get_userbyid(lomowner) AS owner "
		"FROM pg_largeobject_metadata "
		"ORDER BY oid",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *loid = SPI_getvalue(SPI_tuptable->vals[i],
											SPI_tuptable->tupdesc, 1);
			char	   *owner = SPI_getvalue(SPI_tuptable->vals[i],
											 SPI_tuptable->tupdesc, 2);

			if (loid == NULL)
				continue;

			if (i > 0)
				appendStringInfoString(&oid_array, ", ");
			appendStringInfo(&oid_array, "%s::oid", loid);

			appendStringInfo(result,
							 "DO $pg_dbbackup_lo$\n"
							 "BEGIN\n"
							 "  IF EXISTS (SELECT 1 FROM pg_catalog.pg_largeobject_metadata WHERE oid = %s::oid) THEN\n"
							 "    PERFORM pg_catalog.lo_unlink(%s::oid);\n"
							 "  END IF;\n"
							 "  PERFORM pg_catalog.lo_create(%s::oid);\n"
							 "END\n"
							 "$pg_dbbackup_lo$;\n",
							 loid, loid, loid);
			if (owner != NULL)
				appendStringInfo(result,
								 "ALTER LARGE OBJECT %s OWNER TO %s;\n",
								 loid, quote_identifier(owner));
		}
	}
	SPI_finish();

	appendStringInfoString(&oid_array, "]::oid[]");

	appendStringInfo(result,
					 "DO $pg_dbbackup_lo_reconcile$\n"
					 "DECLARE r oid;\n"
					 "BEGIN\n"
					 "  FOR r IN SELECT oid FROM pg_catalog.pg_largeobject_metadata LOOP\n"
					 "    IF NOT (r = ANY(%s)) THEN\n"
					 "      PERFORM pg_catalog.lo_unlink(r);\n"
					 "    END IF;\n"
					 "  END LOOP;\n"
					 "END\n"
					 "$pg_dbbackup_lo_reconcile$;\n",
					 oid_array.data);
	pfree(oid_array.data);

	SPI_connect();
	ret = SPI_execute(
		"SELECT loid::text, pageno::text, encode(data, 'hex') "
		"FROM pg_largeobject "
		"ORDER BY loid, pageno",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *loid = SPI_getvalue(SPI_tuptable->vals[i],
											SPI_tuptable->tupdesc, 1);
			char	   *pageno = SPI_getvalue(SPI_tuptable->vals[i],
											  SPI_tuptable->tupdesc, 2);
			char	   *hex = SPI_getvalue(SPI_tuptable->vals[i],
										   SPI_tuptable->tupdesc, 3);

			if (loid == NULL || pageno == NULL || hex == NULL)
				continue;

			appendStringInfo(result,
							 "SELECT pg_catalog.lo_put(%s::oid, (%s::bigint * 2048), decode(%s, 'hex'));\n",
							 loid, pageno, quote_literal_cstr(hex));
		}
	}
	SPI_finish();

	SPI_connect();
	ret = SPI_execute(
		"SELECT m.oid::text, pg_get_userbyid(a.grantee) AS grantee, "
		"       a.privilege_type, a.is_grantable "
		"FROM pg_largeobject_metadata m, aclexplode(m.lomacl) a "
		"WHERE a.grantee <> 0 "
		"ORDER BY m.oid, a.grantee, a.privilege_type",
		true, 0);

	if (ret == SPI_OK_SELECT)
	{
		for (uint64 i = 0; i < SPI_processed; i++)
		{
			char	   *loid = SPI_getvalue(SPI_tuptable->vals[i],
											SPI_tuptable->tupdesc, 1);
			char	   *grantee = SPI_getvalue(SPI_tuptable->vals[i],
											   SPI_tuptable->tupdesc, 2);
			char	   *priv = SPI_getvalue(SPI_tuptable->vals[i],
											SPI_tuptable->tupdesc, 3);
			char	   *grantable = SPI_getvalue(SPI_tuptable->vals[i],
												 SPI_tuptable->tupdesc, 4);

			if (loid == NULL || grantee == NULL || priv == NULL)
				continue;

			appendStringInfo(result,
							 "GRANT %s ON LARGE OBJECT %s TO %s%s;\n",
							 priv,
							 loid,
							 quote_identifier(grantee),
							 (grantable && strcmp(grantable, "t") == 0)
								 ? " WITH GRANT OPTION" : "");
		}
	}
	SPI_finish();

	return result;
}

StringInfo
metadata_gen_log_tail(Oid db_oid)
{
	StringInfo	result = makeStringInfo();
	StringInfo	part;

	appendStringInfoString(result, "-- === Extensions ===\n");
	part = metadata_gen_extensions(db_oid);
	appendBinaryStringInfo(result, part->data, part->len);

	appendStringInfoString(result, "-- === Referenced roles ===\n");
	part = metadata_gen_roles(db_oid);
	appendBinaryStringInfo(result, part->data, part->len);

	appendStringInfoString(result, "-- === TimescaleDB hypertables ===\n");
	part = metadata_gen_timescaledb(db_oid);
	appendBinaryStringInfo(result, part->data, part->len);

	appendStringInfoString(result, "-- === Object owners ===\n");
	part = metadata_gen_object_owners(db_oid);
	appendBinaryStringInfo(result, part->data, part->len);

	appendStringInfoString(result, "-- === Object grants ===\n");
	part = metadata_gen_object_grants(db_oid);
	appendBinaryStringInfo(result, part->data, part->len);

	appendStringInfoString(result, "-- === Comments ===\n");
	part = metadata_gen_comments(db_oid);
	appendBinaryStringInfo(result, part->data, part->len);

	appendStringInfoString(result, "-- === Large objects ===\n");
	part = metadata_gen_large_objects(db_oid);
	appendBinaryStringInfo(result, part->data, part->len);

	appendStringInfoString(result, "-- === Sequence values ===\n");
	part = metadata_gen_sequence_values(db_oid);
	appendBinaryStringInfo(result, part->data, part->len);

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

	appendStringInfoString(buf, "-- === Referenced roles ===\n");
	part = metadata_gen_roles(db_oid);
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

	appendStringInfoString(buf, "-- === TimescaleDB hypertables ===\n");
	part = metadata_gen_timescaledb(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Object owners ===\n");
	part = metadata_gen_object_owners(db_oid);
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

	appendStringInfoString(buf, "-- === Large objects ===\n");
	part = metadata_gen_large_objects(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	appendStringInfoString(buf, "-- === Sequence values ===\n");
	part = metadata_gen_sequence_values(db_oid);
	appendBinaryStringInfo(buf, part->data, part->len);

	return buf;
}
