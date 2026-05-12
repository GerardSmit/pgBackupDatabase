#include "postgres.h"

#include "access/xact.h"
#include "access/xlog.h"
#include "executor/spi.h"
#include "miscadmin.h"
#include "utils/snapmgr.h"

#include "logical_journal.h"

static bool pgdb_journal_loaded_by_shared_preload = false;
static bool pgdb_journal_in_callback = false;
static bool pgdb_journal_suppressed = false;

static const char *pgdb_journal_sql =
	"DO $pg_dbbackup_journal$\n"
	"DECLARE\n"
	"  r record;\n"
	"  p record;\n"
	"  g record;\n"
	"  last_text text;\n"
	"  called_text text;\n"
	"  script text;\n"
	"  role_sql text;\n"
	"BEGIN\n"
	"  IF current_setting('dbbackup.replaying', true) = 'on' THEN\n"
	"    RETURN;\n"
	"  END IF;\n"
	"\n"
	"  IF to_regclass('dbbackup.logical_chains') IS NULL\n"
	"     OR to_regclass('dbbackup.sequence_state_cache') IS NULL\n"
	"     OR to_regclass('dbbackup.sequence_log') IS NULL\n"
	"     OR to_regclass('dbbackup.large_object_state_cache') IS NULL\n"
	"     OR to_regclass('dbbackup.large_object_log') IS NULL THEN\n"
	"    RETURN;\n"
	"  END IF;\n"
	"\n"
	"  IF NOT EXISTS (\n"
	"      SELECT 1\n"
	"      FROM dbbackup.logical_chains\n"
	"      WHERE db_oid = (SELECT oid FROM pg_database\n"
	"                      WHERE datname = current_database())) THEN\n"
	"    RETURN;\n"
	"  END IF;\n"
	"\n"
	"  FOR r IN\n"
	"    SELECT n.nspname, c.relname\n"
	"    FROM pg_class c\n"
	"    JOIN pg_namespace n ON n.oid = c.relnamespace\n"
	"    WHERE c.relkind = 'S'\n"
	"      AND n.nspname NOT LIKE 'pg\\_%'\n"
	"      AND n.nspname NOT IN ('information_schema', 'dbbackup',\n"
	"          '_timescaledb_internal', '_timescaledb_catalog',\n"
	"          '_timescaledb_config', '_timescaledb_cache',\n"
	"          '_timescaledb_functions', 'timescaledb_information',\n"
	"          'timescaledb_experimental')\n"
	"      AND NOT EXISTS (\n"
	"          SELECT 1 FROM pg_depend d\n"
	"          WHERE d.objid = c.oid AND d.deptype = 'e')\n"
	"    ORDER BY n.nspname, c.relname\n"
	"  LOOP\n"
	"    EXECUTE format('SELECT last_value::text, is_called::text FROM %I.%I',\n"
	"                   r.nspname, r.relname)\n"
	"      INTO last_text, called_text;\n"
	"\n"
	"    IF last_text IS NULL OR called_text IS NULL THEN\n"
	"      CONTINUE;\n"
	"    END IF;\n"
	"\n"
	"    IF NOT EXISTS (\n"
	"        SELECT 1\n"
	"        FROM dbbackup.sequence_state_cache c\n"
	"        WHERE c.schema_name = r.nspname\n"
	"          AND c.sequence_name = r.relname\n"
	"          AND c.last_value = last_text::numeric\n"
	"          AND c.is_called = called_text::boolean) THEN\n"
	"      INSERT INTO dbbackup.sequence_log(\n"
	"          schema_name, sequence_name, last_value, is_called)\n"
	"      VALUES (r.nspname, r.relname,\n"
	"              last_text::numeric, called_text::boolean);\n"
	"\n"
	"      INSERT INTO dbbackup.sequence_state_cache(\n"
	"          schema_name, sequence_name, last_value, is_called)\n"
	"      VALUES (r.nspname, r.relname,\n"
	"              last_text::numeric, called_text::boolean)\n"
	"      ON CONFLICT (schema_name, sequence_name) DO UPDATE SET\n"
	"          last_value = EXCLUDED.last_value,\n"
	"          is_called = EXCLUDED.is_called;\n"
	"    END IF;\n"
	"  END LOOP;\n"
	"\n"
	"  DELETE FROM dbbackup.sequence_state_cache sc\n"
	"  WHERE NOT EXISTS (\n"
	"      SELECT 1\n"
	"      FROM pg_class c\n"
	"      JOIN pg_namespace n ON n.oid = c.relnamespace\n"
	"      WHERE c.relkind = 'S'\n"
	"        AND n.nspname = sc.schema_name\n"
	"        AND c.relname = sc.sequence_name);\n"
	"\n"
	"  FOR r IN\n"
	"    SELECT m.oid::oid AS loid,\n"
	"           md5(coalesce(pg_get_userbyid(m.lomowner), '') || '|' ||\n"
	"               coalesce(m.lomacl::text, '') || '|' ||\n"
	"               coalesce(d.description, '') || '|' ||\n"
	"               coalesce(string_agg(l.pageno::text || ':' ||\n"
	"                                   encode(l.data, 'hex'),\n"
	"                                   ',' ORDER BY l.pageno), '')) AS object_hash\n"
	"    FROM pg_largeobject_metadata m\n"
	"    LEFT JOIN pg_largeobject l ON l.loid = m.oid\n"
	"    LEFT JOIN pg_description d\n"
	"      ON d.classoid = 'pg_largeobject'::regclass\n"
	"     AND d.objoid = m.oid\n"
	"     AND d.objsubid = 0\n"
	"    GROUP BY m.oid, m.lomowner, m.lomacl, d.description\n"
	"    ORDER BY m.oid\n"
	"  LOOP\n"
	"    IF EXISTS (\n"
	"        SELECT 1\n"
	"        FROM dbbackup.large_object_state_cache c\n"
	"        WHERE c.loid = r.loid\n"
	"          AND c.object_hash = r.object_hash) THEN\n"
	"      CONTINUE;\n"
	"    END IF;\n"
	"\n"
	"    script := format('DO $pg_dbbackup_lo$ BEGIN IF EXISTS (SELECT 1 FROM pg_catalog.pg_largeobject_metadata WHERE oid = %s::oid) THEN PERFORM pg_catalog.lo_unlink(%s::oid); END IF; PERFORM pg_catalog.lo_create(%s::oid); END $pg_dbbackup_lo$; ',\n"
	"                     r.loid, r.loid, r.loid);\n"
	"\n"
	"    SELECT CASE WHEN owner_name IS NULL THEN '' ELSE\n"
	"        format('DO $pg_dbbackup_role$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = %L) THEN RAISE WARNING ''pg_dbbackup: role \"%%\" did not exist on restore target; creating NOLOGIN placeholder role without passwords or memberships'', %L; CREATE ROLE %I NOLOGIN; END IF; END $pg_dbbackup_role$; ALTER LARGE OBJECT %s OWNER TO %I; ',\n"
	"               owner_name, owner_name, owner_name, r.loid, owner_name)\n"
	"      END\n"
	"    INTO role_sql\n"
	"    FROM (\n"
	"      SELECT pg_get_userbyid(lomowner) AS owner_name\n"
	"      FROM pg_largeobject_metadata\n"
	"      WHERE oid = r.loid\n"
	"    ) owner_row;\n"
	"    script := script || coalesce(role_sql, '');\n"
	"\n"
	"    FOR p IN\n"
	"      SELECT pageno, encode(data, 'hex') AS hex\n"
	"      FROM pg_largeobject\n"
	"      WHERE loid = r.loid\n"
	"      ORDER BY pageno\n"
	"    LOOP\n"
	"      script := script || format('SELECT pg_catalog.lo_put(%s::oid, (%s::bigint * 2048), decode(%L, ''hex'')); ',\n"
	"                                 r.loid, p.pageno, p.hex);\n"
	"    END LOOP;\n"
	"\n"
	"    FOR g IN\n"
	"      SELECT pg_get_userbyid(a.grantee) AS grantee,\n"
	"             a.privilege_type,\n"
	"             a.is_grantable\n"
	"      FROM pg_largeobject_metadata m,\n"
	"           aclexplode(m.lomacl) a\n"
	"      WHERE m.oid = r.loid\n"
	"        AND a.grantee <> 0\n"
	"      ORDER BY a.grantee, a.privilege_type\n"
	"    LOOP\n"
	"      IF g.grantee IS NULL OR g.privilege_type IS NULL THEN\n"
	"        CONTINUE;\n"
	"      END IF;\n"
	"      script := script || format('DO $pg_dbbackup_role$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = %L) THEN RAISE WARNING ''pg_dbbackup: role \"%%\" did not exist on restore target; creating NOLOGIN placeholder role without passwords or memberships'', %L; CREATE ROLE %I NOLOGIN; END IF; END $pg_dbbackup_role$; GRANT %s ON LARGE OBJECT %s TO %I%s; ',\n"
	"                                 g.grantee, g.grantee, g.grantee,\n"
	"                                 g.privilege_type, r.loid, g.grantee,\n"
	"                                 CASE WHEN g.is_grantable THEN ' WITH GRANT OPTION' ELSE '' END);\n"
	"    END LOOP;\n"
	"\n"
	"    SELECT CASE WHEN d.description IS NULL THEN '' ELSE\n"
	"        format('COMMENT ON LARGE OBJECT %s IS %L; ', r.loid, d.description)\n"
	"      END\n"
	"    INTO role_sql\n"
	"    FROM pg_largeobject_metadata m\n"
	"    LEFT JOIN pg_description d\n"
	"      ON d.classoid = 'pg_largeobject'::regclass\n"
	"     AND d.objoid = m.oid\n"
	"     AND d.objsubid = 0\n"
	"    WHERE m.oid = r.loid;\n"
	"    script := script || coalesce(role_sql, '');\n"
	"\n"
	"    INSERT INTO dbbackup.large_object_log(loid, action, snapshot_sql)\n"
	"    VALUES (r.loid, 'snapshot', script);\n"
	"\n"
	"    INSERT INTO dbbackup.large_object_state_cache(loid, object_hash)\n"
	"    VALUES (r.loid, r.object_hash)\n"
	"    ON CONFLICT (loid) DO UPDATE SET\n"
	"        object_hash = EXCLUDED.object_hash;\n"
	"  END LOOP;\n"
	"\n"
	"  FOR r IN\n"
	"    SELECT c.loid\n"
	"    FROM dbbackup.large_object_state_cache c\n"
	"    LEFT JOIN pg_largeobject_metadata m ON m.oid = c.loid\n"
	"    WHERE m.oid IS NULL\n"
	"    ORDER BY c.loid\n"
	"  LOOP\n"
	"    script := format('DO $pg_dbbackup_lo$ BEGIN IF EXISTS (SELECT 1 FROM pg_catalog.pg_largeobject_metadata WHERE oid = %s::oid) THEN PERFORM pg_catalog.lo_unlink(%s::oid); END IF; END $pg_dbbackup_lo$; ',\n"
	"                     r.loid, r.loid);\n"
	"    INSERT INTO dbbackup.large_object_log(loid, action, snapshot_sql)\n"
	"    VALUES (r.loid, 'unlink', script);\n"
	"    DELETE FROM dbbackup.large_object_state_cache WHERE loid = r.loid;\n"
	"  END LOOP;\n"
	"END\n"
	"$pg_dbbackup_journal$";

static const char *pgdb_journal_reset_sql =
	"DO $pg_dbbackup_journal_reset$\n"
	"DECLARE\n"
	"  r record;\n"
	"  last_text text;\n"
	"  called_text text;\n"
	"BEGIN\n"
	"  IF to_regclass('dbbackup.sequence_state_cache') IS NULL\n"
	"     OR to_regclass('dbbackup.large_object_state_cache') IS NULL THEN\n"
	"    RETURN;\n"
	"  END IF;\n"
	"\n"
	"  TRUNCATE dbbackup.sequence_state_cache,\n"
	"           dbbackup.large_object_state_cache;\n"
	"\n"
	"  FOR r IN\n"
	"    SELECT n.nspname, c.relname\n"
	"    FROM pg_class c\n"
	"    JOIN pg_namespace n ON n.oid = c.relnamespace\n"
	"    WHERE c.relkind = 'S'\n"
	"      AND n.nspname NOT LIKE 'pg\\_%'\n"
	"      AND n.nspname NOT IN ('information_schema', 'dbbackup',\n"
	"          '_timescaledb_internal', '_timescaledb_catalog',\n"
	"          '_timescaledb_config', '_timescaledb_cache',\n"
	"          '_timescaledb_functions', 'timescaledb_information',\n"
	"          'timescaledb_experimental')\n"
	"      AND NOT EXISTS (\n"
	"          SELECT 1 FROM pg_depend d\n"
	"          WHERE d.objid = c.oid AND d.deptype = 'e')\n"
	"    ORDER BY n.nspname, c.relname\n"
	"  LOOP\n"
	"    EXECUTE format('SELECT last_value::text, is_called::text FROM %I.%I',\n"
	"                   r.nspname, r.relname)\n"
	"      INTO last_text, called_text;\n"
	"    IF last_text IS NOT NULL AND called_text IS NOT NULL THEN\n"
	"      INSERT INTO dbbackup.sequence_state_cache(\n"
	"          schema_name, sequence_name, last_value, is_called)\n"
	"      VALUES (r.nspname, r.relname,\n"
	"              last_text::numeric, called_text::boolean);\n"
	"    END IF;\n"
	"  END LOOP;\n"
	"\n"
	"  INSERT INTO dbbackup.large_object_state_cache(loid, object_hash)\n"
	"  SELECT m.oid::oid,\n"
	"         md5(coalesce(pg_get_userbyid(m.lomowner), '') || '|' ||\n"
	"             coalesce(m.lomacl::text, '') || '|' ||\n"
	"             coalesce(d.description, '') || '|' ||\n"
	"             coalesce(string_agg(l.pageno::text || ':' ||\n"
	"                                 encode(l.data, 'hex'),\n"
	"                                 ',' ORDER BY l.pageno), ''))\n"
	"  FROM pg_largeobject_metadata m\n"
	"  LEFT JOIN pg_largeobject l ON l.loid = m.oid\n"
	"  LEFT JOIN pg_description d\n"
	"    ON d.classoid = 'pg_largeobject'::regclass\n"
	"   AND d.objoid = m.oid\n"
	"   AND d.objsubid = 0\n"
	"  GROUP BY m.oid, m.lomowner, m.lomacl, d.description;\n"
	"END\n"
	"$pg_dbbackup_journal_reset$";

static void
pgdb_logical_journal_xact_callback(XactEvent event, void *arg)
{
	int			ret;
	bool		pushed_snapshot = false;

	(void) arg;

	if (event != XACT_EVENT_PRE_COMMIT)
	{
		if (event == XACT_EVENT_COMMIT ||
			event == XACT_EVENT_PARALLEL_COMMIT ||
			event == XACT_EVENT_ABORT ||
			event == XACT_EVENT_PARALLEL_ABORT ||
			event == XACT_EVENT_PREPARE)
			pgdb_journal_suppressed = false;
		return;
	}
	if (pgdb_journal_in_callback)
		return;
	if (pgdb_journal_suppressed)
		return;
	if (!OidIsValid(MyDatabaseId))
		return;
	if (RecoveryInProgress() || XactReadOnly)
		return;

	pgdb_journal_in_callback = true;
	PG_TRY();
	{
		PushActiveSnapshot(GetTransactionSnapshot());
		pushed_snapshot = true;

		if (SPI_connect() != SPI_OK_CONNECT)
			ereport(ERROR,
					(errcode(ERRCODE_INTERNAL_ERROR),
					 errmsg("pg_dbbackup journal could not connect SPI")));

		ret = SPI_execute(pgdb_journal_sql, false, 0);
		SPI_finish();
		PopActiveSnapshot();
		pushed_snapshot = false;

		if (ret != SPI_OK_UTILITY)
			ereport(ERROR,
					(errcode(ERRCODE_INTERNAL_ERROR),
					 errmsg("pg_dbbackup journal failed (rc=%d)", ret)));
	}
	PG_CATCH();
	{
		if (pushed_snapshot)
			PopActiveSnapshot();
		pgdb_journal_in_callback = false;
		PG_RE_THROW();
	}
	PG_END_TRY();
	pgdb_journal_in_callback = false;
}

void
pgdb_logical_journal_init(bool loaded_by_shared_preload)
{
	pgdb_journal_loaded_by_shared_preload = loaded_by_shared_preload;
	RegisterXactCallback(pgdb_logical_journal_xact_callback, NULL);
}

bool
pgdb_logical_journal_loaded_by_shared_preload(void)
{
	return pgdb_journal_loaded_by_shared_preload;
}

void
pgdb_logical_journal_set_suppressed(bool suppressed)
{
	pgdb_journal_suppressed = suppressed;
}

void
pgdb_logical_journal_reset_state(void)
{
	int			ret;

	if (SPI_connect() != SPI_OK_CONNECT)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("pg_dbbackup journal reset could not connect SPI")));

	ret = SPI_execute(pgdb_journal_reset_sql, false, 0);
	SPI_finish();

	if (ret != SPI_OK_UTILITY)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("pg_dbbackup journal state reset failed (rc=%d)",
						ret)));
}
