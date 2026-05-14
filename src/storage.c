#include "postgres.h"

#include <ctype.h>
#include <sys/stat.h>
#include <unistd.h>

#include "access/xact.h"
#include "access/xlogdefs.h"
#include "catalog/pg_database.h"
#include "catalog/pg_type.h"
#include "common/cryptohash.h"
#include "commands/dbcommands.h"
#include "executor/spi.h"
#include "fmgr.h"
#include "miscadmin.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/jsonb.h"
#include "utils/memutils.h"
#include "utils/numeric.h"
#include "utils/timestamp.h"
#include "utils/uuid.h"

#include "backup_full.h"
#include "backup_simple.h"
#include "bakfile.h"
#include "pg_dbbackup.h"
#include "restore_full.h"
#include "restore_simple.h"
#include "s3_client.h"

PG_FUNCTION_INFO_V1(pg_dbbackup_to_storage);
PG_FUNCTION_INFO_V1(pg_dbrestore_from_storage_impl);
PG_FUNCTION_INFO_V1(pg_dbbackup_s3_create_bucket);
PG_FUNCTION_INFO_V1(pg_dbbackup_s3_object_exists);
PG_FUNCTION_INFO_V1(pg_dbbackup_s3_delete_object);
PG_FUNCTION_INFO_V1(pg_dbbackup_refresh_storage_catalog);

typedef struct StorageTarget
{
	char	   *name;
	PgdbS3Config s3;
} StorageTarget;

typedef struct PreviousArtifact
{
	bool		found;
	char	   *backup_id;
	char	   *chain_id;
	char	   *object_key;
	char	   *sha256;
	uint64		size_bytes;
	TimestampTz created_at;
} PreviousArtifact;

typedef struct RestoreArtifact
{
	char	   *object_key;
	char	   *sha256;
	uint64		size_bytes;
} RestoreArtifact;

typedef struct ImportedManifest
{
	char	   *backup_id;
	char	   *dbname;
	char	   *backup_type;
	char	   *mode;
	char	   *chain_id;
	char	   *previous_backup_id;
	char	   *object_key;
	char	   *object_uri;
	char	   *manifest_key;
	char	   *manifest_uri;
	uint64		size_bytes;
	char	   *sha256;
	char	   *created_at;
	char	   *start_lsn;
	char	   *stop_lsn;
	char	   *base_lsn;
	bool		encrypted;
} ImportedManifest;

static const char *
backup_type_name(PgDbBackupType type)
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
	return "full";
}

static const char *
backup_mode_name(PgDbBackupMode mode)
{
	return mode == BACKUP_MODE_FULL ? "full" : "simple";
}

static pg_uuid_t
generate_uuid_v4(void)
{
	pg_uuid_t	id;
	int			i;

	if (!pg_strong_random(id.data, UUID_LEN))
	{
		for (i = 0; i < UUID_LEN; i++)
			id.data[i] = (uint8) (random() & 0xff);
	}

	id.data[6] = (id.data[6] & 0x0f) | 0x40;
	id.data[8] = (id.data[8] & 0x3f) | 0x80;
	return id;
}

static char *
uuid_to_cstring(const pg_uuid_t *id)
{
	return psprintf(
		"%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
		id->data[0], id->data[1], id->data[2], id->data[3],
		id->data[4], id->data[5],
		id->data[6], id->data[7],
		id->data[8], id->data[9],
		id->data[10], id->data[11], id->data[12], id->data[13],
		id->data[14], id->data[15]);
}

static char *
spi_get_text_copy(HeapTuple tuple, TupleDesc tupdesc, int col,
				  MemoryContext ctx)
{
	char	   *v = SPI_getvalue(tuple, tupdesc, col);
	char	   *copy = NULL;

	if (v != NULL)
	{
		MemoryContext old = MemoryContextSwitchTo(ctx);

		copy = pstrdup(v);
		MemoryContextSwitchTo(old);
	}
	return copy;
}

static StorageTarget *
load_s3_target(const char *target_name)
{
	StorageTarget *target;
	MemoryContext caller_ctx = CurrentMemoryContext;
	int			ret;
	bool		isnull;
	Datum		d;

	SPI_connect();
	ret = SPI_execute_with_args(
		"SELECT target_type, bucket, prefix, region, endpoint_url, "
		"       force_path_style, encryption, kms_key_id, "
		"       object_lock_mode, object_lock_retain_until, "
		"       max_retries, connect_timeout_ms, request_timeout_ms, "
		"       bandwidth_limit_bps "
		"FROM dbbackup.storage_targets WHERE name = $1",
		1,
		(Oid[]){TEXTOID},
		(Datum[]){CStringGetTextDatum(target_name)},
		NULL, true, 1);

	if (ret != SPI_OK_SELECT)
	{
		SPI_finish();
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not load storage target \"%s\" (rc=%d)",
						target_name, ret)));
	}
	if (SPI_processed != 1)
	{
		SPI_finish();
		ereport(ERROR,
				(errcode(ERRCODE_UNDEFINED_OBJECT),
				 errmsg("storage target \"%s\" does not exist", target_name)));
	}

	{
		HeapTuple	tuple = SPI_tuptable->vals[0];
		TupleDesc	tupdesc = SPI_tuptable->tupdesc;
		char	   *target_type = SPI_getvalue(tuple, tupdesc, 1);
		MemoryContext old = MemoryContextSwitchTo(caller_ctx);

		if (target_type == NULL ||
			(strcmp(target_type, "s3") != 0 &&
			 strcmp(target_type, "s3-compatible") != 0))
		{
			SPI_finish();
			ereport(ERROR,
					(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
					 errmsg("storage target \"%s\" is not an S3 target",
							target_name)));
		}

		target = palloc0(sizeof(StorageTarget));
		target->name = pstrdup(target_name);
		target->s3.bucket = spi_get_text_copy(tuple, tupdesc, 2, caller_ctx);
		target->s3.prefix = spi_get_text_copy(tuple, tupdesc, 3, caller_ctx);
		target->s3.region = spi_get_text_copy(tuple, tupdesc, 4, caller_ctx);
		target->s3.endpoint_url = spi_get_text_copy(tuple, tupdesc, 5, caller_ctx);
		target->s3.encryption = spi_get_text_copy(tuple, tupdesc, 7, caller_ctx);
		target->s3.kms_key_id = spi_get_text_copy(tuple, tupdesc, 8, caller_ctx);
		target->s3.object_lock_mode = spi_get_text_copy(tuple, tupdesc, 9, caller_ctx);
		target->s3.object_lock_retain_until = spi_get_text_copy(tuple, tupdesc, 10, caller_ctx);

		d = SPI_getbinval(tuple, tupdesc, 6, &isnull);
		target->s3.force_path_style = !isnull && DatumGetBool(d);

		d = SPI_getbinval(tuple, tupdesc, 11, &isnull);
		target->s3.max_retries = isnull
			? pgdb_s3_default_max_retries : DatumGetInt32(d);
		d = SPI_getbinval(tuple, tupdesc, 12, &isnull);
		target->s3.connect_timeout_ms = isnull
			? pgdb_s3_default_connect_timeout_ms : DatumGetInt32(d);
		d = SPI_getbinval(tuple, tupdesc, 13, &isnull);
		target->s3.request_timeout_ms = isnull
			? pgdb_s3_default_request_timeout_ms : DatumGetInt32(d);
		d = SPI_getbinval(tuple, tupdesc, 14, &isnull);
		target->s3.bandwidth_limit_bps = isnull
			? (long) pgdb_s3_default_bandwidth_limit_kbps * 1024L
			: DatumGetInt64(d);

		if (target->s3.prefix == NULL)
			target->s3.prefix = pstrdup("");
		if (target->s3.region == NULL || target->s3.region[0] == '\0')
		{
			const char *env_region = getenv("AWS_REGION");

			target->s3.region = pstrdup(env_region && env_region[0]
										? env_region : "us-east-1");
		}
		if (target->s3.encryption == NULL)
			target->s3.encryption = pstrdup("none");
		if (target->s3.object_lock_mode == NULL)
			target->s3.object_lock_mode = pstrdup("off");

		MemoryContextSwitchTo(old);
	}

	SPI_finish();

	if (target->s3.bucket == NULL || target->s3.bucket[0] == '\0')
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("S3 storage target \"%s\" has no bucket", target_name)));

	return target;
}

static char *
resolve_target_name(const char *storage_target, const char *backup_set)
{
	MemoryContext caller_ctx = CurrentMemoryContext;
	char	   *resolved = NULL;
	int			ret;

	if (storage_target && storage_target[0])
		return pstrdup(storage_target);

	if (backup_set == NULL || backup_set[0] == '\0')
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("storage_target or backup_set is required")));

	SPI_connect();
	ret = SPI_execute_with_args(
		"SELECT storage_target FROM dbbackup.backup_sets WHERE name = $1",
		1,
		(Oid[]){TEXTOID},
		(Datum[]){CStringGetTextDatum(backup_set)},
		NULL, true, 1);
	if (ret != SPI_OK_SELECT || SPI_processed != 1)
	{
		SPI_finish();
		ereport(ERROR,
				(errcode(ERRCODE_UNDEFINED_OBJECT),
				 errmsg("backup set \"%s\" does not exist", backup_set)));
	}

	resolved = spi_get_text_copy(SPI_tuptable->vals[0],
								 SPI_tuptable->tupdesc, 1, caller_ctx);
	SPI_finish();
	return resolved;
}

static char *
sanitize_key_component(const char *input)
{
	StringInfoData out;
	const unsigned char *p = (const unsigned char *) input;

	initStringInfo(&out);
	while (*p)
	{
		unsigned char c = *p++;

		if (isalnum(c) || c == '_' || c == '-' || c == '.')
			appendStringInfoChar(&out, (char) c);
		else
			appendStringInfoChar(&out, '_');
	}
	return out.data;
}

static char *
utc_stamp(void)
{
	time_t		now = time(NULL);
	struct tm	tm;
	char		buf[32];

#ifdef _WIN32
	gmtime_s(&tm, &now);
#else
	gmtime_r(&now, &tm);
#endif
	strftime(buf, sizeof(buf), "%Y%m%dT%H%M%SZ", &tm);
	return pstrdup(buf);
}

static void
format_lsn_text(XLogRecPtr lsn, char *buf, size_t buflen)
{
	snprintf(buf, buflen, "%X/%X", (uint32) (lsn >> 32), (uint32) lsn);
}

static void
file_sha256_hex(const char *path, char hex_out[65], uint64 *size_out)
{
	pg_cryptohash_ctx *ctx = pg_cryptohash_create(PG_SHA256);
	FILE	   *fp;
	char	   *buf;
	size_t		n;
	uint64		total = 0;
	uint8		digest[32];
	static const char hex[] = "0123456789abcdef";
	int			i;

	if (ctx == NULL || pg_cryptohash_init(ctx) < 0)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not initialize SHA-256 context")));

	fp = AllocateFile(path, "rb");
	if (fp == NULL)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not open \"%s\" for hashing: %m", path)));

	buf = palloc(1024 * 1024);
	while ((n = fread(buf, 1, 1024 * 1024, fp)) > 0)
	{
		CHECK_FOR_INTERRUPTS();
		if (pg_cryptohash_update(ctx, (uint8 *) buf, n) < 0)
			ereport(ERROR,
					(errcode(ERRCODE_INTERNAL_ERROR),
					 errmsg("could not update SHA-256 context")));
		total += n;
	}
	if (ferror(fp))
	{
		pfree(buf);
		FreeFile(fp);
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not read \"%s\" while hashing: %m", path)));
	}

	pfree(buf);
	FreeFile(fp);

	if (pg_cryptohash_final(ctx, digest, sizeof(digest)) < 0)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not finalize SHA-256 context")));
	pg_cryptohash_free(ctx);

	for (i = 0; i < 32; i++)
	{
		hex_out[i * 2] = hex[digest[i] >> 4];
		hex_out[i * 2 + 1] = hex[digest[i] & 0x0f];
	}
	hex_out[64] = '\0';
	if (size_out)
		*size_out = total;
}

static char *
json_escape(const char *input)
{
	StringInfoData out;
	const unsigned char *p = (const unsigned char *) input;

	initStringInfo(&out);
	while (*p)
	{
		unsigned char c = *p++;

		switch (c)
		{
			case '\\':
				appendStringInfoString(&out, "\\\\");
				break;
			case '"':
				appendStringInfoString(&out, "\\\"");
				break;
			case '\n':
				appendStringInfoString(&out, "\\n");
				break;
			case '\r':
				appendStringInfoString(&out, "\\r");
				break;
			case '\t':
				appendStringInfoString(&out, "\\t");
				break;
			default:
				if (c < 0x20)
					appendStringInfo(&out, "\\u%04x", c);
				else
					appendStringInfoChar(&out, (char) c);
				break;
		}
	}
	return out.data;
}

static char *
make_manifest_json(const char *backup_id, const char *target_name,
				   const char *dbname, const char *type_name,
				   const char *mode_name, const char *object_key,
				   const char *object_uri, const char *manifest_key,
				   uint64 size_bytes, const char sha_hex[65],
				   BakFileHeader *header, const char *chain_id,
				   const char *previous_backup_id)
{
	StringInfoData json;
	char		start_lsn[64];
	char		stop_lsn[64];
	char		base_lsn[64];
	char	   *created_at = timestamptz_to_str((TimestampTz) header->created_at);

	format_lsn_text((XLogRecPtr) header->start_lsn, start_lsn, sizeof(start_lsn));
	format_lsn_text((XLogRecPtr) header->stop_lsn, stop_lsn, sizeof(stop_lsn));
	format_lsn_text((XLogRecPtr) header->base_backup_lsn, base_lsn, sizeof(base_lsn));

	initStringInfo(&json);
	appendStringInfoString(&json, "{\n");
	appendStringInfo(&json, "  \"format\": \"pg_dbbackup.s3.manifest.v1\",\n");
	appendStringInfo(&json, "  \"backup_id\": \"%s\",\n", backup_id);
	appendStringInfo(&json, "  \"storage_target\": \"%s\",\n", json_escape(target_name));
	appendStringInfo(&json, "  \"dbname\": \"%s\",\n", json_escape(dbname));
	appendStringInfo(&json, "  \"backup_type\": \"%s\",\n", type_name);
	appendStringInfo(&json, "  \"mode\": \"%s\",\n", mode_name);
	appendStringInfo(&json, "  \"chain_id\": \"%s\",\n", chain_id);
	if (previous_backup_id)
		appendStringInfo(&json, "  \"previous_backup_id\": \"%s\",\n",
						 previous_backup_id);
	appendStringInfo(&json, "  \"object_key\": \"%s\",\n", json_escape(object_key));
	appendStringInfo(&json, "  \"object_uri\": \"%s\",\n", json_escape(object_uri));
	appendStringInfo(&json, "  \"manifest_key\": \"%s\",\n", json_escape(manifest_key));
	appendStringInfo(&json, "  \"size_bytes\": " UINT64_FORMAT ",\n", size_bytes);
	appendStringInfo(&json, "  \"sha256\": \"%s\",\n", sha_hex);
	appendStringInfo(&json, "  \"created_at\": \"%s\",\n", json_escape(created_at));
	appendStringInfo(&json, "  \"start_lsn\": \"%s\",\n", start_lsn);
	appendStringInfo(&json, "  \"stop_lsn\": \"%s\",\n", stop_lsn);
	appendStringInfo(&json, "  \"base_lsn\": \"%s\",\n", base_lsn);
	appendStringInfo(&json, "  \"compressed\": %s,\n",
					 header->compressed ? "true" : "false");
	appendStringInfo(&json, "  \"encrypted\": %s\n",
					 header->encrypted ? "true" : "false");
	appendStringInfoString(&json, "}\n");

	return json.data;
}

static bool
string_ends_with(const char *s, const char *suffix)
{
	size_t		s_len;
	size_t		suffix_len;

	if (s == NULL || suffix == NULL)
		return false;
	s_len = strlen(s);
	suffix_len = strlen(suffix);
	if (suffix_len > s_len)
		return false;
	return strcmp(s + s_len - suffix_len, suffix) == 0;
}

static char *
xml_unescape_text(const char *input)
{
	StringInfoData out;
	const char *p = input;

	initStringInfo(&out);
	while (*p)
	{
		if (strncmp(p, "&amp;", 5) == 0)
		{
			appendStringInfoChar(&out, '&');
			p += 5;
		}
		else if (strncmp(p, "&lt;", 4) == 0)
		{
			appendStringInfoChar(&out, '<');
			p += 4;
		}
		else if (strncmp(p, "&gt;", 4) == 0)
		{
			appendStringInfoChar(&out, '>');
			p += 4;
		}
		else if (strncmp(p, "&quot;", 6) == 0)
		{
			appendStringInfoChar(&out, '"');
			p += 6;
		}
		else if (strncmp(p, "&apos;", 6) == 0)
		{
			appendStringInfoChar(&out, '\'');
			p += 6;
		}
		else
			appendStringInfoChar(&out, *p++);
	}
	return out.data;
}

static char *
strip_target_prefix(PgdbS3Config *config, const char *key)
{
	size_t		prefix_len;

	if (config->prefix == NULL || config->prefix[0] == '\0')
		return pstrdup(key);

	prefix_len = strlen(config->prefix);
	while (prefix_len > 0 && config->prefix[prefix_len - 1] == '/')
		prefix_len--;
	if (prefix_len == 0)
		return pstrdup(key);

	if (strncmp(key, config->prefix, prefix_len) == 0 &&
		key[prefix_len] == '/')
		return pstrdup(key + prefix_len + 1);

	return pstrdup(key);
}

static JsonbValue *
manifest_json_lookup(Jsonb *jb, const char *key)
{
	JsonbValue	keyv;

	keyv.type = jbvString;
	keyv.val.string.val = (char *) key;
	keyv.val.string.len = strlen(key);
	return findJsonbValueFromContainer(&jb->root, JB_FOBJECT, &keyv);
}

static char *
manifest_json_text(Jsonb *jb, const char *key, bool required)
{
	JsonbValue *value = manifest_json_lookup(jb, key);

	if (value == NULL || value->type == jbvNull)
	{
		if (required)
			ereport(ERROR,
					(errcode(ERRCODE_DATA_CORRUPTED),
					 errmsg("S3 backup manifest is missing field \"%s\"", key)));
		return NULL;
	}
	if (value->type != jbvString)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("S3 backup manifest field \"%s\" is not a string", key)));
	return pnstrdup(value->val.string.val, value->val.string.len);
}

static bool
manifest_json_bool(Jsonb *jb, const char *key, bool dflt)
{
	JsonbValue *value = manifest_json_lookup(jb, key);

	if (value == NULL || value->type == jbvNull)
		return dflt;
	if (value->type != jbvBool)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("S3 backup manifest field \"%s\" is not a boolean", key)));
	return value->val.boolean;
}

static uint64
manifest_json_uint64(Jsonb *jb, const char *key)
{
	JsonbValue *value = manifest_json_lookup(jb, key);
	Datum		num_datum;
	char	   *str;

	if (value == NULL || value->type == jbvNull)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("S3 backup manifest is missing field \"%s\"", key)));
	if (value->type != jbvNumeric)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("S3 backup manifest field \"%s\" is not numeric", key)));

	num_datum = DirectFunctionCall1(numeric_out,
								   NumericGetDatum(value->val.numeric));
	str = DatumGetCString(num_datum);
	return (uint64) strtoull(str, NULL, 10);
}

static void
parse_imported_manifest(const char *json_text, ImportedManifest *manifest)
{
	Datum		jb_datum;
	Jsonb	   *jb;
	char	   *format;

	memset(manifest, 0, sizeof(ImportedManifest));
	jb_datum = DirectFunctionCall1(jsonb_in, CStringGetDatum(json_text));
	jb = DatumGetJsonbP(jb_datum);

	format = manifest_json_text(jb, "format", true);
	if (strcmp(format, "pg_dbbackup.s3.manifest.v1") != 0)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("unsupported S3 backup manifest format \"%s\"", format)));

	manifest->backup_id = manifest_json_text(jb, "backup_id", true);
	manifest->dbname = manifest_json_text(jb, "dbname", true);
	manifest->backup_type = manifest_json_text(jb, "backup_type", true);
	manifest->mode = manifest_json_text(jb, "mode", true);
	manifest->chain_id = manifest_json_text(jb, "chain_id", true);
	manifest->previous_backup_id =
		manifest_json_text(jb, "previous_backup_id", false);
	manifest->object_key = manifest_json_text(jb, "object_key", true);
	manifest->object_uri = manifest_json_text(jb, "object_uri", true);
	manifest->manifest_key = manifest_json_text(jb, "manifest_key", true);
	manifest->manifest_uri = psprintf("%s.manifest.json",
									  manifest->object_uri);
	manifest->size_bytes = manifest_json_uint64(jb, "size_bytes");
	manifest->sha256 = manifest_json_text(jb, "sha256", true);
	manifest->created_at = manifest_json_text(jb, "created_at", true);
	manifest->start_lsn = manifest_json_text(jb, "start_lsn", true);
	manifest->stop_lsn = manifest_json_text(jb, "stop_lsn", true);
	manifest->base_lsn = manifest_json_text(jb, "base_lsn", true);
	manifest->encrypted = manifest_json_bool(jb, "encrypted", false);
}

static bool
upsert_imported_artifact(const char *target_name,
						 const ImportedManifest *manifest,
						 const char *status)
{
	Oid			argtypes[20] = {
		TEXTOID, TEXTOID, TEXTOID, TEXTOID, TEXTOID,
		TEXTOID, TEXTOID, TEXTOID, TEXTOID, TEXTOID,
		TEXTOID, TEXTOID, TEXTOID, TEXTOID, TEXTOID,
		TEXTOID, INT8OID, TEXTOID, TEXTOID, BOOLOID
	};
	Datum		values[20];
	char		nulls[20];
	int			ret;

	memset(nulls, ' ', sizeof(nulls));
	values[0] = CStringGetTextDatum(manifest->backup_id);
	values[1] = CStringGetTextDatum(target_name);
	values[2] = CStringGetTextDatum(manifest->dbname);
	values[3] = CStringGetTextDatum(manifest->backup_type);
	values[4] = CStringGetTextDatum(manifest->mode);
	values[5] = CStringGetTextDatum(manifest->chain_id);
	values[6] = manifest->previous_backup_id
		? CStringGetTextDatum(manifest->previous_backup_id) : (Datum) 0;
	if (manifest->previous_backup_id == NULL)
		nulls[6] = 'n';
	values[7] = CStringGetTextDatum(manifest->object_key);
	values[8] = CStringGetTextDatum(manifest->object_uri);
	values[9] = CStringGetTextDatum(manifest->manifest_key);
	values[10] = CStringGetTextDatum(manifest->manifest_uri);
	values[11] = CStringGetTextDatum(manifest->start_lsn);
	values[12] = CStringGetTextDatum(manifest->stop_lsn);
	values[13] = CStringGetTextDatum(manifest->base_lsn);
	values[14] = CStringGetTextDatum(manifest->created_at);
	values[15] = CStringGetTextDatum(manifest->created_at);
	values[16] = Int64GetDatum((int64) manifest->size_bytes);
	values[17] = CStringGetTextDatum(manifest->sha256);
	values[18] = CStringGetTextDatum(status);
	values[19] = BoolGetDatum(manifest->encrypted);

	SPI_connect();
	ret = SPI_execute_with_args(
		"INSERT INTO dbbackup.backup_artifacts "
		"(backup_id, backup_set, storage_target, dbname, backup_type, mode, "
		" chain_id, previous_backup_id, object_key, object_uri, manifest_key, "
		" manifest_uri, start_lsn, stop_lsn, base_lsn, range_start_time, "
		" range_end_time, size_bytes, sha256, status, encrypted, local_path) "
		"VALUES ($1::uuid, NULL, $2, $3, $4, $5, $6::uuid, $7::uuid, "
		"        $8, $9, $10, $11, $12::pg_lsn, $13::pg_lsn, $14::pg_lsn, "
		"        $15::timestamptz, $16::timestamptz, $17, $18, $19, $20, NULL) "
		"ON CONFLICT (backup_id) DO UPDATE SET "
		"  storage_target = EXCLUDED.storage_target, "
		"  dbname = EXCLUDED.dbname, "
		"  backup_type = EXCLUDED.backup_type, "
		"  mode = EXCLUDED.mode, "
		"  chain_id = EXCLUDED.chain_id, "
		"  previous_backup_id = EXCLUDED.previous_backup_id, "
		"  object_key = EXCLUDED.object_key, "
		"  object_uri = EXCLUDED.object_uri, "
		"  manifest_key = EXCLUDED.manifest_key, "
		"  manifest_uri = EXCLUDED.manifest_uri, "
		"  start_lsn = EXCLUDED.start_lsn, "
		"  stop_lsn = EXCLUDED.stop_lsn, "
		"  base_lsn = EXCLUDED.base_lsn, "
		"  range_end_time = EXCLUDED.range_end_time, "
		"  size_bytes = EXCLUDED.size_bytes, "
		"  sha256 = EXCLUDED.sha256, "
		"  status = EXCLUDED.status, "
		"  encrypted = EXCLUDED.encrypted",
		20, argtypes, values, nulls, false, 0);
	SPI_finish();

	if (ret != SPI_OK_INSERT)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not import S3 backup manifest (rc=%d)", ret)));

	CommandCounterIncrement();
	return true;
}

static void
refresh_imported_ranges(const char *target_name, const char *dbname)
{
	int			ret;

	SPI_connect();
	ret = SPI_execute_with_args(
		"UPDATE dbbackup.backup_artifacts cur "
		"   SET range_start_time = prev.range_end_time "
		"  FROM dbbackup.backup_artifacts prev "
		" WHERE cur.storage_target = $1 "
		"   AND cur.dbname = $2 "
		"   AND cur.previous_backup_id = prev.backup_id "
		"   AND cur.range_start_time = cur.range_end_time",
		2,
		(Oid[]){TEXTOID, TEXTOID},
		(Datum[]){CStringGetTextDatum(target_name),
				  CStringGetTextDatum(dbname)},
		NULL, false, 0);
	SPI_finish();

	if (ret != SPI_OK_UPDATE)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not refresh imported S3 backup ranges (rc=%d)",
						ret)));

	CommandCounterIncrement();
}

static int
refresh_storage_catalog_internal(const char *target_name, const char *dbname)
{
	StorageTarget *target;
	StringInfoData listing;
	char	   *db_key;
	char	   *prefix;
	const char *scan;
	int			imported = 0;

	target = load_s3_target(target_name);
	db_key = sanitize_key_component(dbname);
	prefix = psprintf("db/%s/chains/", db_key);

	initStringInfo(&listing);
	pgdb_s3_list_prefix(&target->s3, prefix, &listing);

	scan = listing.data;
	while ((scan = strstr(scan, "<Key>")) != NULL)
	{
		const char *start = scan + strlen("<Key>");
		const char *end = strstr(start, "</Key>");
		char	   *full_key;
		char	   *unescaped_key;
		char	   *manifest_key;
		char	   *manifest_json;
		ImportedManifest manifest;
		PgdbS3HeadResult head;
		const char *status;

		if (end == NULL)
			break;
		full_key = pnstrdup(start, end - start);
		unescaped_key = xml_unescape_text(full_key);
		scan = end + strlen("</Key>");

		if (!string_ends_with(unescaped_key, ".manifest.json"))
			continue;

		manifest_key = strip_target_prefix(&target->s3, unescaped_key);
		manifest_json = pgdb_s3_get_text(&target->s3, manifest_key);
		parse_imported_manifest(manifest_json, &manifest);

		if (strcmp(manifest.dbname, dbname) != 0)
			continue;

		head = pgdb_s3_head_object(&target->s3, manifest.object_key);
		status = head.exists && head.size_bytes == manifest.size_bytes
			? "available" : "missing";
		if (upsert_imported_artifact(target_name, &manifest, status))
			imported++;
	}

	if (imported > 0)
	{
		CommandCounterIncrement();
		refresh_imported_ranges(target_name, dbname);
	}

	return imported;
}

static PreviousArtifact
load_previous_artifact(const char *storage_target, const char *dbname,
					   PgDbBackupType backup_type, const char *base_id)
{
	PreviousArtifact prev;
	MemoryContext caller_ctx = CurrentMemoryContext;
	int			ret;
	const char *sql;
	Oid			argtypes[4];
	Datum		values[4];
	int			nargs;

	memset(&prev, 0, sizeof(prev));

	if (base_id && base_id[0])
	{
		sql =
			"SELECT backup_id::text, chain_id::text, object_key, range_end_time, "
			"       sha256, size_bytes "
			"FROM dbbackup.backup_artifacts "
			"WHERE backup_id = $1::uuid AND status = 'available'";
		argtypes[0] = TEXTOID;
		values[0] = CStringGetTextDatum(base_id);
		nargs = 1;
	}
	else if (backup_type == BACKUP_TYPE_DIFFERENTIAL)
	{
		sql =
			"SELECT backup_id::text, chain_id::text, object_key, range_end_time, "
			"       sha256, size_bytes "
			"FROM dbbackup.backup_artifacts "
			"WHERE storage_target = $1 AND dbname = $2 "
			"  AND backup_type = 'full' AND status = 'available' "
			"ORDER BY range_end_time DESC, inserted_at DESC LIMIT 1";
		argtypes[0] = TEXTOID;
		argtypes[1] = TEXTOID;
		values[0] = CStringGetTextDatum(storage_target);
		values[1] = CStringGetTextDatum(dbname);
		nargs = 2;
	}
	else
	{
		sql =
			"SELECT backup_id::text, chain_id::text, object_key, range_end_time, "
			"       sha256, size_bytes "
			"FROM dbbackup.backup_artifacts "
			"WHERE storage_target = $1 AND dbname = $2 "
			"  AND status = 'available' "
			"ORDER BY range_end_time DESC, inserted_at DESC LIMIT 1";
		argtypes[0] = TEXTOID;
		argtypes[1] = TEXTOID;
		values[0] = CStringGetTextDatum(storage_target);
		values[1] = CStringGetTextDatum(dbname);
		nargs = 2;
	}

	SPI_connect();
	ret = SPI_execute_with_args(sql, nargs, argtypes, values, NULL, true, 1);
	if (ret != SPI_OK_SELECT)
	{
		SPI_finish();
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not find previous storage backup (rc=%d)", ret)));
	}
	if (SPI_processed == 0)
	{
		SPI_finish();
		return prev;
	}

	prev.found = true;
	prev.backup_id = spi_get_text_copy(SPI_tuptable->vals[0],
									   SPI_tuptable->tupdesc, 1, caller_ctx);
	prev.chain_id = spi_get_text_copy(SPI_tuptable->vals[0],
									  SPI_tuptable->tupdesc, 2, caller_ctx);
	prev.object_key = spi_get_text_copy(SPI_tuptable->vals[0],
										SPI_tuptable->tupdesc, 3, caller_ctx);
	prev.sha256 = spi_get_text_copy(SPI_tuptable->vals[0],
									SPI_tuptable->tupdesc, 5, caller_ctx);
	{
		bool		isnull;
		Datum		d;

		prev.created_at = DatumGetTimestampTz(SPI_getbinval(SPI_tuptable->vals[0],
															SPI_tuptable->tupdesc,
															4, &isnull));
		if (isnull)
			prev.created_at = 0;
		d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc,
						  6, &isnull);
		prev.size_bytes = isnull ? 0 : (uint64) DatumGetInt64(d);
	}
	SPI_finish();
	return prev;
}

static char *
make_temp_path(const char *backup_id, const char *suffix)
{
	return psprintf("/tmp/pg_dbbackup_%s_%s.bak", backup_id, suffix);
}

static void
insert_artifact_row(const pg_uuid_t *backup_id, const char *backup_id_text,
					const char *backup_set, const char *target_name,
					const char *dbname, const char *type_name,
					const char *mode_name, const char *object_key,
					const char *object_uri, const char *manifest_key,
					const char *manifest_uri, const char *chain_id,
					const char *previous_backup_id,
					TimestampTz range_start_time,
					BakFileHeader *header, uint64 size_bytes,
					const char sha_hex[65], const char *local_path)
{
	Oid			argtypes[21] = {
		UUIDOID, TEXTOID, TEXTOID, TEXTOID, TEXTOID, TEXTOID,
		TEXTOID, TEXTOID, TEXTOID, TEXTOID, TEXTOID, TEXTOID,
		TEXTOID, TEXTOID, TIMESTAMPTZOID, TIMESTAMPTZOID, INT8OID,
		TEXTOID, TEXTOID, BOOLOID, TEXTOID
	};
	Datum		values[21];
	char		nulls[21];
	char		start_lsn[64];
	char		stop_lsn[64];
	char		base_lsn[64];
	int			ret;

	memset(nulls, ' ', sizeof(nulls));
	format_lsn_text((XLogRecPtr) header->start_lsn, start_lsn, sizeof(start_lsn));
	format_lsn_text((XLogRecPtr) header->stop_lsn, stop_lsn, sizeof(stop_lsn));
	format_lsn_text((XLogRecPtr) header->base_backup_lsn, base_lsn, sizeof(base_lsn));

	values[0] = UUIDPGetDatum(backup_id);
	values[1] = backup_set ? CStringGetTextDatum(backup_set) : (Datum) 0;
	if (!backup_set)
		nulls[1] = 'n';
	values[2] = CStringGetTextDatum(target_name);
	values[3] = CStringGetTextDatum(dbname);
	values[4] = CStringGetTextDatum(type_name);
	values[5] = CStringGetTextDatum(mode_name);
	values[6] = CStringGetTextDatum(chain_id);
	values[7] = previous_backup_id ? CStringGetTextDatum(previous_backup_id) : (Datum) 0;
	if (!previous_backup_id)
		nulls[7] = 'n';
	values[8] = CStringGetTextDatum(object_key);
	values[9] = CStringGetTextDatum(object_uri);
	values[10] = CStringGetTextDatum(manifest_key);
	values[11] = CStringGetTextDatum(manifest_uri);
	values[12] = CStringGetTextDatum(start_lsn);
	values[13] = CStringGetTextDatum(stop_lsn);
	values[14] = TimestampTzGetDatum(range_start_time);
	values[15] = TimestampTzGetDatum((TimestampTz) header->created_at);
	values[16] = Int64GetDatum((int64) size_bytes);
	values[17] = CStringGetTextDatum(sha_hex);
	values[18] = CStringGetTextDatum(base_lsn);
	values[19] = BoolGetDatum(header->encrypted);
	values[20] = local_path ? CStringGetTextDatum(local_path) : (Datum) 0;
	if (!local_path)
		nulls[20] = 'n';

	SPI_connect();
	ret = SPI_execute_with_args(
		"INSERT INTO dbbackup.backup_artifacts "
		"(backup_id, backup_set, storage_target, dbname, backup_type, mode, "
		" chain_id, previous_backup_id, object_key, object_uri, manifest_key, "
		" manifest_uri, start_lsn, stop_lsn, range_start_time, range_end_time, "
		" size_bytes, sha256, base_lsn, status, encrypted, local_path) "
		"VALUES ($1, $2, $3, $4, $5, $6, $7::uuid, $8::uuid, "
		"        $9, $10, $11, $12, $13::pg_lsn, $14::pg_lsn, "
		"        $15, $16, $17, $18, $19::pg_lsn, 'available', $20, $21)",
		21, argtypes, values, nulls, false, 0);
	SPI_finish();

	if (ret != SPI_OK_INSERT)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not insert backup artifact row (rc=%d)", ret)));
}

static void
insert_storage_upload_row(const char *upload_id, const char *target_name,
						  const char *object_key,
						  const pg_uuid_t *backup_id)
{
	Oid			argtypes[4] = {TEXTOID, TEXTOID, TEXTOID, UUIDOID};
	Datum		values[4];
	int			ret;

	values[0] = CStringGetTextDatum(upload_id);
	values[1] = CStringGetTextDatum(target_name);
	values[2] = CStringGetTextDatum(object_key);
	values[3] = UUIDPGetDatum(backup_id);

	SPI_connect();
	ret = SPI_execute_with_args(
		"INSERT INTO dbbackup.storage_uploads "
		"(upload_id, storage_target, object_key, backup_id, status) "
		"VALUES ($1, $2, $3, $4, 'running')",
		4, argtypes, values, NULL, false, 0);
	SPI_finish();

	if (ret != SPI_OK_INSERT)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not insert S3 multipart upload row (rc=%d)",
						ret)));
}

static void
mark_storage_upload_completed(const char *upload_id)
{
	Oid			argtypes[1] = {TEXTOID};
	Datum		values[1];
	int			ret;

	values[0] = CStringGetTextDatum(upload_id);
	SPI_connect();
	ret = SPI_execute_with_args(
		"UPDATE dbbackup.storage_uploads "
		"   SET status = 'completed', updated_at = now() "
		" WHERE upload_id = $1",
		1, argtypes, values, NULL, false, 0);
	SPI_finish();

	if (ret != SPI_OK_UPDATE)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not update S3 multipart upload row (rc=%d)",
						ret)));
}

static bool
upload_artifact_file(StorageTarget *target, const char *target_name,
					 const pg_uuid_t *backup_id, const char *object_key,
					 const char *filepath, uint64 size_bytes)
{
	char	   *upload_id = NULL;
	PgdbS3MultipartPart *parts = NULL;
	int			part_count;
	uint64		part_size = PGDB_S3_MULTIPART_PART_SIZE_BYTES;
	int			i;

	if (size_bytes <= PGDB_S3_MULTIPART_THRESHOLD_BYTES)
	{
		pgdb_s3_put_file(&target->s3, object_key, filepath);
		return false;
	}

	part_count = (int) ((size_bytes + part_size - 1) / part_size);
	if (part_count > 10000)
		ereport(ERROR,
				(errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
				 errmsg("backup is too large for configured S3 multipart part size"),
				 errdetail("Object needs %d parts; S3 allows at most 10000.",
						   part_count)));

	PG_TRY();
	{
		upload_id = pgdb_s3_create_multipart_upload(&target->s3, object_key);
		insert_storage_upload_row(upload_id, target_name, object_key,
								  backup_id);

		parts = palloc0(sizeof(PgdbS3MultipartPart) * part_count);
		for (i = 0; i < part_count; i++)
		{
			uint64		offset = part_size * (uint64) i;
			uint64		length = size_bytes - offset;

			CHECK_FOR_INTERRUPTS();
			if (length > part_size)
				length = part_size;

			parts[i].part_number = i + 1;
			parts[i].etag = pgdb_s3_upload_part(&target->s3, object_key,
												 upload_id, i + 1,
												 filepath, offset, length);
		}

		pgdb_s3_complete_multipart_upload(&target->s3, object_key,
										  upload_id, parts, part_count);
		mark_storage_upload_completed(upload_id);
	}
	PG_CATCH();
	{
		ErrorData  *edata;
		MemoryContext old_ctx;

		old_ctx = MemoryContextSwitchTo(TopMemoryContext);
		edata = CopyErrorData();
		MemoryContextSwitchTo(old_ctx);
		FlushErrorState();

		if (upload_id)
		{
			PG_TRY();
			{
				pgdb_s3_abort_multipart_upload(&target->s3, object_key,
											   upload_id);
			}
			PG_CATCH();
			{
				FlushErrorState();
			}
			PG_END_TRY();
		}
		ReThrowError(edata);
	}
	PG_END_TRY();

	return true;
}

Datum
pg_dbbackup_s3_create_bucket(PG_FUNCTION_ARGS)
{
	char	   *target_name = text_to_cstring(PG_GETARG_TEXT_PP(0));
	StorageTarget *target;

	if (!superuser())
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
				 errmsg("must be superuser to manage S3 backup buckets")));

	target = load_s3_target(target_name);
	pgdb_s3_create_bucket(&target->s3);
	PG_RETURN_VOID();
}

Datum
pg_dbbackup_s3_object_exists(PG_FUNCTION_ARGS)
{
	char	   *target_name = text_to_cstring(PG_GETARG_TEXT_PP(0));
	char	   *object_key = text_to_cstring(PG_GETARG_TEXT_PP(1));
	StorageTarget *target;
	PgdbS3HeadResult head;

	if (!superuser())
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
				 errmsg("must be superuser to inspect S3 backup objects")));

	target = load_s3_target(target_name);
	head = pgdb_s3_head_object(&target->s3, object_key);
	PG_RETURN_BOOL(head.exists);
}

Datum
pg_dbbackup_s3_delete_object(PG_FUNCTION_ARGS)
{
	char	   *target_name = text_to_cstring(PG_GETARG_TEXT_PP(0));
	char	   *object_key = text_to_cstring(PG_GETARG_TEXT_PP(1));
	StorageTarget *target;

	if (!superuser())
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
				 errmsg("must be superuser to delete S3 backup objects")));

	target = load_s3_target(target_name);
	pgdb_s3_delete_object(&target->s3, object_key);
	PG_RETURN_VOID();
}

Datum
pg_dbbackup_refresh_storage_catalog(PG_FUNCTION_ARGS)
{
	char	   *target_name = text_to_cstring(PG_GETARG_TEXT_PP(0));
	char	   *dbname = text_to_cstring(PG_GETARG_TEXT_PP(1));
	int			imported;

	if (!superuser())
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
				 errmsg("must be superuser to refresh S3 backup catalogs")));

	imported = refresh_storage_catalog_internal(target_name, dbname);
	PG_RETURN_INT32(imported);
}

Datum
pg_dbbackup_to_storage(PG_FUNCTION_ARGS)
{
	char	   *dbname = text_to_cstring(PG_GETARG_TEXT_PP(0));
	char	   *type_arg = text_to_cstring(PG_GETARG_TEXT_PP(1));
	char	   *storage_target_arg = PG_ARGISNULL(2) ? NULL :
		text_to_cstring(PG_GETARG_TEXT_PP(2));
	char	   *backup_set = PG_ARGISNULL(3) ? NULL :
		text_to_cstring(PG_GETARG_TEXT_PP(3));
	bool		do_compress = PG_GETARG_BOOL(4);
	char	   *password = PG_ARGISNULL(5) ? NULL :
		text_to_cstring(PG_GETARG_TEXT_PP(5));
	char	   *base_id = PG_ARGISNULL(6) ? NULL :
		uuid_to_cstring(PG_GETARG_UUID_P(6));
	PgDbBackupType backup_type = pgdb_parse_backup_type(type_arg);
	char	   *target_name;
	StorageTarget *target;
	pg_uuid_t	backup_id;
	char	   *backup_id_text;
	char	   *tmp_path;
	char	   *prev_tmp_path = NULL;
	PreviousArtifact prev;
	Oid			db_oid;
	PgDbBackupMode mode;
	BakFileReader *reader;
	BakFileHeader header;
	char		sha_hex[65];
	uint64		size_bytes = 0;
	char	   *db_key;
	char	   *chain_id;
	char	   *stamp;
	char	   *object_key;
	char	   *manifest_key;
	char	   *object_uri;
	char	   *manifest_uri;
	char	   *manifest_json;
	TimestampTz range_start_time;
	PgdbS3HeadResult head;
	pg_uuid_t  *ret_id;
	PgDbBackupDeferredAdvance deferred_advance;
	bool		has_deferred_advance = false;

	if (!superuser())
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
				 errmsg("must be superuser to perform storage backups")));

	{
		Datum		routed_result;

		if (pgdb_route_to_primary_if_standby(fcinfo, false, &routed_result))
			PG_RETURN_DATUM(routed_result);
	}

	target_name = resolve_target_name(storage_target_arg, backup_set);
	target = load_s3_target(target_name);
	db_oid = get_database_oid(dbname, false);
	mode = pg_dbbackup_resolve_mode(db_oid);

	if (mode == BACKUP_MODE_SIMPLE && backup_type == BACKUP_TYPE_LOG)
		ereport(ERROR,
				(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
				 errmsg("log backups require FULL recovery mode"),
				 errhint("Use pg_dbbackup_set_mode('%s', 'full') first.",
						 dbname)));

	backup_id = generate_uuid_v4();
	backup_id_text = uuid_to_cstring(&backup_id);
	tmp_path = make_temp_path(backup_id_text, "artifact");

	memset(&prev, 0, sizeof(prev));
	if (backup_type != BACKUP_TYPE_FULL)
	{
		prev = load_previous_artifact(target_name, dbname, backup_type, base_id);
		if (!prev.found)
			ereport(ERROR,
					(errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
					 errmsg("no previous storage backup found for %s backup",
							backup_type_name(backup_type))));
		prev_tmp_path = make_temp_path(backup_id_text, "previous");
		pgdb_s3_get_file(&target->s3, prev.object_key, prev_tmp_path);
		{
			char		prev_sha[65];
			uint64		prev_size = 0;

			file_sha256_hex(prev_tmp_path, prev_sha, &prev_size);
			if (prev.size_bytes != prev_size || prev.sha256 == NULL ||
				strcmp(prev.sha256, prev_sha) != 0)
				ereport(ERROR,
						(errcode(ERRCODE_DATA_CORRUPTED),
						 errmsg("downloaded previous S3 backup failed checksum verification"),
						 errdetail("Object key: %s", prev.object_key)));
		}
	}

	memset(&deferred_advance, 0, sizeof(deferred_advance));
	PG_TRY();
	{
	if (mode == BACKUP_MODE_SIMPLE)
	{
		if (backup_type == BACKUP_TYPE_FULL)
			backup_simple_full(db_oid, dbname, tmp_path, do_compress, password);
		else
			backup_simple_differential(db_oid, dbname, tmp_path,
									   prev_tmp_path, do_compress, password);
	}
	else
	{
		if (backup_type == BACKUP_TYPE_FULL)
		{
			backup_full_full_deferred(db_oid, dbname, tmp_path,
									  do_compress, password,
									  &deferred_advance);
			has_deferred_advance = true;
		}
		else if (backup_type == BACKUP_TYPE_DIFFERENTIAL)
		{
			backup_full_differential_deferred(db_oid, dbname, tmp_path,
											  prev_tmp_path, do_compress,
											  password, &deferred_advance);
			has_deferred_advance = true;
		}
		else
		{
			backup_full_log_deferred(db_oid, dbname, tmp_path, prev_tmp_path,
									 do_compress, password,
									 &deferred_advance);
			has_deferred_advance = true;
		}
	}

	reader = bakfile_open(tmp_path, password);
	memcpy(&header, &reader->header, sizeof(header));
	bakfile_close_reader(reader);
	file_sha256_hex(tmp_path, sha_hex, &size_bytes);

	db_key = sanitize_key_component(dbname);
	chain_id = backup_type == BACKUP_TYPE_FULL ? backup_id_text : prev.chain_id;
	stamp = utc_stamp();
	object_key = psprintf("db/%s/chains/%s/%s_%s_%s.bak",
						  db_key, chain_id, stamp, backup_id_text,
						  backup_type_name(backup_type));
	manifest_key = psprintf("%s.manifest.json", object_key);
	object_uri = psprintf("s3://%s/%s%s%s",
						  target->s3.bucket,
						  target->s3.prefix && target->s3.prefix[0]
						  ? target->s3.prefix : "",
						  target->s3.prefix && target->s3.prefix[0]
						  ? "/" : "",
						  object_key);
	manifest_uri = psprintf("%s.manifest.json", object_uri);
	manifest_json = make_manifest_json(backup_id_text, target_name, dbname,
									   backup_type_name(backup_type),
									   backup_mode_name(mode), object_key,
									   object_uri, manifest_key, size_bytes,
									   sha_hex, &header, chain_id,
									   prev.found ? prev.backup_id : NULL);

	upload_artifact_file(target, target_name, &backup_id, object_key,
						 tmp_path, size_bytes);
	head = pgdb_s3_head_object(&target->s3, object_key);
	if (!head.exists || head.size_bytes != size_bytes)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("uploaded S3 object size verification failed"),
				 errdetail("Expected " UINT64_FORMAT " bytes, got " UINT64_FORMAT ".",
						   size_bytes, head.size_bytes)));

	pgdb_s3_put_text(&target->s3, manifest_key, manifest_json);
	head = pgdb_s3_head_object(&target->s3, manifest_key);
	if (!head.exists)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("uploaded S3 manifest verification failed")));

	if (has_deferred_advance)
		backup_full_finish_deferred_advance(db_oid, dbname,
											&deferred_advance);

	range_start_time = prev.found ? prev.created_at : (TimestampTz) header.created_at;
	insert_artifact_row(&backup_id, backup_id_text, backup_set, target_name,
						dbname, backup_type_name(backup_type),
						backup_mode_name(mode), object_key, object_uri,
						manifest_key, manifest_uri, chain_id,
						prev.found ? prev.backup_id : NULL, range_start_time,
						&header, size_bytes, sha_hex, NULL);
	}
	PG_CATCH();
	{
		ErrorData  *edata;
		MemoryContext old_ctx;

		old_ctx = MemoryContextSwitchTo(TopMemoryContext);
		edata = CopyErrorData();
		MemoryContextSwitchTo(old_ctx);
		FlushErrorState();
		if (has_deferred_advance)
			backup_full_abort_deferred_advance(&deferred_advance);
		if (prev_tmp_path)
			(void) unlink(prev_tmp_path);
		if (tmp_path)
			(void) unlink(tmp_path);
		ReThrowError(edata);
	}
	PG_END_TRY();

	if (has_deferred_advance)
		backup_full_free_deferred_advance(&deferred_advance);

	if (prev_tmp_path)
		(void) unlink(prev_tmp_path);
	if (tmp_path)
		(void) unlink(tmp_path);

	ret_id = (pg_uuid_t *) palloc(sizeof(pg_uuid_t));
	memcpy(ret_id, &backup_id, sizeof(pg_uuid_t));
	PG_RETURN_UUID_P(ret_id);
}

static RestoreArtifact *
load_restore_artifacts(const char *target_name, const char *dbname,
					   TimestampTz stop_at, bool has_stop_at,
					   int *count_out)
{
	RestoreArtifact *items = NULL;
	MemoryContext caller_ctx = CurrentMemoryContext;
	int			ret;
	uint64		i;

	*count_out = 0;
	SPI_connect();
	if (has_stop_at)
	{
		ret = SPI_execute_with_args(
			"WITH full_backup AS ("
			"  SELECT chain_id FROM dbbackup.backup_artifacts "
			"  WHERE storage_target = $1 AND dbname = $2 "
			"    AND backup_type = 'full' AND status = 'available' "
			"    AND range_end_time <= $3 "
			"  ORDER BY range_end_time DESC, inserted_at DESC LIMIT 1"
			") "
			"SELECT object_key, sha256, size_bytes FROM dbbackup.backup_artifacts a "
			"JOIN full_backup f ON f.chain_id = a.chain_id "
			"WHERE a.storage_target = $1 AND a.dbname = $2 "
			"  AND a.status = 'available' "
			"  AND (a.range_end_time <= $3 "
			"       OR (a.range_start_time <= $3 AND a.range_end_time >= $3)) "
			"ORDER BY a.range_end_time, a.inserted_at",
			3,
			(Oid[]){TEXTOID, TEXTOID, TIMESTAMPTZOID},
			(Datum[]){CStringGetTextDatum(target_name),
					  CStringGetTextDatum(dbname),
					  TimestampTzGetDatum(stop_at)},
			NULL, true, 0);
	}
	else
	{
		ret = SPI_execute_with_args(
			"WITH latest_chain AS ("
			"  SELECT chain_id FROM dbbackup.backup_artifacts "
			"  WHERE storage_target = $1 AND dbname = $2 "
			"    AND backup_type = 'full' AND status = 'available' "
			"  ORDER BY range_end_time DESC, inserted_at DESC LIMIT 1"
			") "
			"SELECT object_key, sha256, size_bytes FROM dbbackup.backup_artifacts a "
			"JOIN latest_chain l ON l.chain_id = a.chain_id "
			"WHERE a.storage_target = $1 AND a.dbname = $2 "
			"  AND a.status = 'available' "
			"ORDER BY a.range_end_time, a.inserted_at",
			2,
			(Oid[]){TEXTOID, TEXTOID},
			(Datum[]){CStringGetTextDatum(target_name),
					  CStringGetTextDatum(dbname)},
			NULL, true, 0);
	}

	if (ret != SPI_OK_SELECT)
	{
		SPI_finish();
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not search storage backups (rc=%d)", ret)));
	}
	if (SPI_processed == 0)
	{
		SPI_finish();
		ereport(ERROR,
				(errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
				 errmsg("no restorable S3 backup chain found for database \"%s\"",
						dbname)));
	}

	{
		MemoryContext old = MemoryContextSwitchTo(caller_ctx);

		items = palloc0(sizeof(RestoreArtifact) * SPI_processed);
		MemoryContextSwitchTo(old);
	}

	for (i = 0; i < SPI_processed; i++)
	{
		bool		isnull;
		Datum		d;

		items[i].object_key = spi_get_text_copy(SPI_tuptable->vals[i],
												 SPI_tuptable->tupdesc,
												 1, caller_ctx);
		items[i].sha256 = spi_get_text_copy(SPI_tuptable->vals[i],
											 SPI_tuptable->tupdesc,
											 2, caller_ctx);
		d = SPI_getbinval(SPI_tuptable->vals[i], SPI_tuptable->tupdesc,
						  3, &isnull);
		items[i].size_bytes = isnull ? 0 : (uint64) DatumGetInt64(d);
	}

	*count_out = (int) SPI_processed;
	SPI_finish();
	return items;
}

Datum
pg_dbrestore_from_storage_impl(PG_FUNCTION_ARGS)
{
	char	   *dbname = text_to_cstring(PG_GETARG_TEXT_PP(0));
	char	   *target_name = text_to_cstring(PG_GETARG_TEXT_PP(1));
	char	   *target_db = PG_ARGISNULL(2) ? NULL :
		text_to_cstring(PG_GETARG_TEXT_PP(2));
	TimestampTz stop_at = PG_ARGISNULL(3) ? 0 : PG_GETARG_TIMESTAMPTZ(3);
	bool		has_stop_at = !PG_ARGISNULL(3);
	char	   *password = PG_ARGISNULL(4) ? NULL :
		text_to_cstring(PG_GETARG_TEXT_PP(4));
	StorageTarget *target;
	RestoreArtifact *items;
	int			count;
	char	  **files;
	int			i;
	BakFileReader *probe;
	PgDbBackupMode mode;

	if (!superuser())
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
				 errmsg("must be superuser to perform storage restores")));

	target = load_s3_target(target_name);
	items = load_restore_artifacts(target_name, dbname, stop_at, has_stop_at,
								   &count);
	files = palloc0(sizeof(char *) * count);

	for (i = 0; i < count; i++)
	{
		char		download_sha[65];
		uint64		download_size = 0;

		files[i] = make_temp_path(sanitize_key_component(dbname),
								  psprintf("restore_%d", i));
		pgdb_s3_get_file(&target->s3, items[i].object_key, files[i]);
		file_sha256_hex(files[i], download_sha, &download_size);
		if (items[i].size_bytes != download_size ||
			items[i].sha256 == NULL ||
			strcmp(items[i].sha256, download_sha) != 0)
			ereport(ERROR,
					(errcode(ERRCODE_DATA_CORRUPTED),
					 errmsg("downloaded S3 backup artifact failed checksum verification"),
					 errdetail("Object key: %s", items[i].object_key)));
	}

	probe = bakfile_open(files[0], password);
	mode = probe->header.mode;
	bakfile_close_reader(probe);

	if (mode == BACKUP_MODE_FULL || has_stop_at)
		restore_full(target_db ? target_db : dbname, files, count,
					 stop_at, has_stop_at, password);
	else
		restore_simple(target_db ? target_db : dbname, files, count, password);

	for (i = 0; i < count; i++)
		(void) unlink(files[i]);

	PG_RETURN_VOID();
}
