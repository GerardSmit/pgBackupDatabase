#include "postgres.h"

#include <sys/stat.h>
#include <unistd.h>

#include "access/xlog.h"
#include "access/xlogdefs.h"
#include "catalog/pg_database.h"
#include "common/cryptohash.h"
#include "executor/spi.h"
#include "lib/stringinfo.h"
#include "miscadmin.h"
#include "port/pg_bswap.h"
#include "storage/fd.h"
#include "storage/lmgr.h"
#include "utils/builtins.h"
#include "utils/timestamp.h"

#include "backup_simple.h"
#include "bakfile.h"
#include "metadata_gen.h"
#include "ddl_gen.h"

static char *
make_copy_tempfile_path(void)
{
	uint32		r1 = (uint32) random();
	uint32		r2 = (uint32) random();

	return psprintf("/tmp/pg_dbbackup_copy_%u_%u.bin", r1, r2);
}

static void
copy_table_to_buffer(const char *schema, const char *relname,
					  char **buf_out, size_t *len_out)
{
	char	   *tmp_path = make_copy_tempfile_path();
	StringInfoData copy_sql;
	int			ret;
	FILE	   *fp;
	struct stat st;
	char	   *buf = NULL;
	size_t		filesize;

	initStringInfo(&copy_sql);
	/*
	 * Use COPY (SELECT * FROM ...) rather than COPY <table> ... so that
	 * TimescaleDB hypertables (where the parent relation's own heap is
	 * empty and rows live in chunks) export their full contents via the
	 * planner, not just the parent's heap.
	 */
	appendStringInfo(&copy_sql,
					 "COPY (SELECT * FROM %s.%s) TO %s (FORMAT binary)",
					 quote_identifier(schema),
					 quote_identifier(relname),
					 quote_literal_cstr(tmp_path));

	ret = SPI_execute(copy_sql.data, false, 0);
	pfree(copy_sql.data);
	if (ret != SPI_OK_UTILITY)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("COPY ... TO file failed for %s.%s (SPI rc=%d)",
						schema, relname, ret)));

	if (stat(tmp_path, &st) != 0)
	{
		int			save_errno = errno;
		(void) unlink(tmp_path);
		errno = save_errno;
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not stat COPY output \"%s\": %m", tmp_path)));
	}
	filesize = (size_t) st.st_size;

	buf = palloc(filesize > 0 ? filesize : 1);

	fp = AllocateFile(tmp_path, "rb");
	if (fp == NULL)
	{
		(void) unlink(tmp_path);
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not open COPY output \"%s\": %m", tmp_path)));
	}

	if (filesize > 0 && fread(buf, 1, filesize, fp) != filesize)
	{
		int			save_errno = errno;
		FreeFile(fp);
		(void) unlink(tmp_path);
		errno = save_errno;
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not read COPY output \"%s\": %m", tmp_path)));
	}

	FreeFile(fp);
	(void) unlink(tmp_path);
	pfree(tmp_path);

	*buf_out = buf;
	*len_out = filesize;
}

typedef struct TableId
{
	char	   *schema;
	char	   *relname;
} TableId;

static TableId *
collect_user_tables(uint32 *count_out)
{
	int			ret;
	uint64		i;
	TableId	   *list;

	ret = SPI_execute(
		"SELECT n.nspname, c.relname "
		"FROM pg_class c "
		"JOIN pg_namespace n ON c.relnamespace = n.oid "
		"WHERE c.relkind = 'r' "
		"AND n.nspname NOT LIKE 'pg\\_%' "
		"AND n.nspname NOT IN ('information_schema', 'dbbackup', "
		"'_timescaledb_internal', '_timescaledb_catalog', "
		"'_timescaledb_config', '_timescaledb_cache', "
		"'_timescaledb_functions', 'timescaledb_information', "
		"'timescaledb_experimental') "
		"AND c.oid NOT IN "
		"(SELECT d.objid FROM pg_depend d WHERE d.deptype = 'e') "
		"ORDER BY c.oid",
		true, 0);

	if (ret != SPI_OK_SELECT)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("failed to enumerate user tables (rc=%d)", ret)));

	*count_out = (uint32) SPI_processed;
	if (SPI_processed == 0)
		return NULL;

	list = SPI_palloc(sizeof(TableId) * SPI_processed);

	for (i = 0; i < SPI_processed; i++)
	{
		char	   *nsp = SPI_getvalue(SPI_tuptable->vals[i],
									   SPI_tuptable->tupdesc, 1);
		char	   *rel = SPI_getvalue(SPI_tuptable->vals[i],
									   SPI_tuptable->tupdesc, 2);
		size_t		nlen = strlen(nsp);
		size_t		rlen = strlen(rel);

		list[i].schema = SPI_palloc(nlen + 1);
		memcpy(list[i].schema, nsp, nlen + 1);
		list[i].relname = SPI_palloc(rlen + 1);
		memcpy(list[i].relname, rel, rlen + 1);
	}

	return list;
}

static XLogRecPtr
current_lsn(void)
{
	return GetXLogWriteRecPtr();
}

static uint32
current_tli(void)
{
	return GetWALInsertionTimeLine();
}

void
backup_simple_full(Oid db_oid, const char *db_name, const char *filepath,
				   bool compress, const char *password)
{
	BakFileHeader header;
	BakFileWriter *writer;
	StringInfo	metadata_sql;
	StringInfo	schema_sql;
	TableId	   *tables;
	uint32		table_count = 0;
	XLogRecPtr	start_lsn;
	XLogRecPtr	stop_lsn;
	FILE	   *probe;

	if (!superuser())
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
				 errmsg("must be superuser to perform backups")));

	probe = AllocateFile(filepath, "wb");
	if (probe == NULL)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not open backup destination \"%s\": %m",
						filepath)));
	FreeFile(probe);
	(void) unlink(filepath);

	LockSharedObject(DatabaseRelationId, db_oid, 0, AccessShareLock);

	start_lsn = current_lsn();

	memset(&header, 0, sizeof(header));
	header.format_version = BAKFILE_VERSION;
	header.mode = BACKUP_MODE_SIMPLE;
	header.type = BACKUP_TYPE_FULL;
	strlcpy(header.db_name, db_name, sizeof(header.db_name));
	header.db_oid = db_oid;
	header.start_lsn = (uint64) start_lsn;
	header.stop_lsn = (uint64) start_lsn;
	header.start_tli = current_tli();
	header.stop_tli = header.start_tli;
	header.pg_version = PG_VERSION_NUM;
	header.created_at = GetCurrentTimestamp();
	header.base_backup_lsn = 0;
	header.compressed = compress;
	header.encrypted = (password != NULL);
	header.file_count = 0;

	metadata_sql = metadata_gen_all(db_oid);
	schema_sql = ddl_gen_all(db_oid);

	SPI_connect();
	tables = collect_user_tables(&table_count);
	header.file_count = table_count;

	writer = bakfile_create(filepath, &header, compress, password);

	bakfile_begin_section(writer, BAKSECTION_METADATA);
	if (metadata_sql && metadata_sql->len > 0)
		bakfile_write_section_data(writer, metadata_sql->data,
								   metadata_sql->len);
	bakfile_end_section(writer);

	bakfile_begin_section(writer, BAKSECTION_SCHEMA);
	if (schema_sql && schema_sql->len > 0)
		bakfile_write_section_data(writer, schema_sql->data,
								   schema_sql->len);
	bakfile_end_section(writer);

	bakfile_begin_section(writer, BAKSECTION_DATA);
	{
		uint32		net_count = pg_hton32(table_count);

		bakfile_write_section_data(writer, &net_count, sizeof(net_count));
	}

	for (uint32 i = 0; i < table_count; i++)
	{
		char	   *copy_buf = NULL;
		size_t		copy_len = 0;
		char	   *entry_path;

		CHECK_FOR_INTERRUPTS();

		ereport(NOTICE,
				(errmsg("backing up table %u/%u: %s.%s",
						i + 1, table_count,
						tables[i].schema, tables[i].relname)));

		copy_table_to_buffer(tables[i].schema, tables[i].relname,
							 &copy_buf, &copy_len);

		entry_path = psprintf("%s.%s", tables[i].schema, tables[i].relname);
		bakfile_begin_data_entry(writer, entry_path, (uint64) copy_len);
		if (copy_len > 0)
			bakfile_write_data_entry_chunk(writer, copy_buf, copy_len);
		bakfile_end_data_entry(writer);

		pfree(entry_path);
		if (copy_buf)
			pfree(copy_buf);
	}

	bakfile_end_section(writer);

	SPI_finish();

	stop_lsn = current_lsn();
	(void) stop_lsn;

	bakfile_close(writer);

	UnlockSharedObject(DatabaseRelationId, db_oid, 0, AccessShareLock);

	ereport(NOTICE,
			(errmsg("SIMPLE FULL backup of \"%s\" complete: %u tables written to \"%s\"",
					db_name, table_count, filepath)));
}

static void
sha256_buffer(const void *data, size_t len, uint8 digest[32])
{
	pg_cryptohash_ctx *ctx = pg_cryptohash_create(PG_SHA256);

	if (ctx == NULL || pg_cryptohash_init(ctx) < 0)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not init SHA-256 context")));
	if (len > 0 && pg_cryptohash_update(ctx, data, len) < 0)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not update SHA-256 context")));
	if (pg_cryptohash_final(ctx, digest, 32) < 0)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not finalize SHA-256 context")));
	pg_cryptohash_free(ctx);
}

typedef struct BaseEntryDigest
{
	char	   *path;
	uint8		digest[32];
} BaseEntryDigest;

static BaseEntryDigest *
read_base_full_digests(const char *base_filepath, const char *password,
					   uint32 *count_out, uint64 *base_stop_lsn_out,
					   char *base_db_name_out, size_t base_db_name_sz)
{
	BakFileReader *reader;
	uint8		stype;
	BakDataEntryMeta *metas;
	uint32		meta_count = 0;
	BaseEntryDigest *out;
	uint32		i;

	reader = bakfile_open(base_filepath, password);

	if (reader->header.mode != BACKUP_MODE_SIMPLE ||
		reader->header.type != BACKUP_TYPE_FULL)
	{
		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("base backup \"%s\" is not a SIMPLE FULL .bak",
						base_filepath)));
	}

	if (base_stop_lsn_out)
		*base_stop_lsn_out = reader->header.stop_lsn;
	if (base_db_name_out && base_db_name_sz > 0)
		strlcpy(base_db_name_out, reader->header.db_name, base_db_name_sz);

	/* METADATA -> skip */
	stype = bakfile_next_section(reader);
	if (stype != BAKSECTION_METADATA)
	{
		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("base .bak: expected METADATA section, got %u", stype)));
	}

	/* SCHEMA -> skip */
	stype = bakfile_next_section(reader);
	if (stype != BAKSECTION_SCHEMA)
	{
		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("base .bak: expected SCHEMA section, got %u", stype)));
	}

	/* DATA */
	stype = bakfile_next_section(reader);
	if (stype != BAKSECTION_DATA)
	{
		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("base .bak: expected DATA section, got %u", stype)));
	}

	metas = bakfile_list_data_entries(reader, &meta_count);

	out = meta_count > 0 ? palloc0(sizeof(BaseEntryDigest) * meta_count) : NULL;
	for (i = 0; i < meta_count; i++)
	{
		out[i].path = pstrdup(metas[i].path);
		memcpy(out[i].digest, metas[i].digest, 32);
		pfree(metas[i].path);
	}
	if (metas)
		pfree(metas);

	bakfile_close_reader(reader);

	*count_out = meta_count;
	return out;
}

static const BaseEntryDigest *
find_base_entry(const BaseEntryDigest *base, uint32 base_count,
				const char *path)
{
	uint32		i;

	for (i = 0; i < base_count; i++)
	{
		if (strcmp(base[i].path, path) == 0)
			return &base[i];
	}
	return NULL;
}

typedef struct ChangedTable
{
	char	   *entry_path;
	char	   *copy_buf;
	size_t		copy_len;
} ChangedTable;

void
backup_simple_differential(Oid db_oid, const char *db_name,
						   const char *filepath, const char *base_filepath,
						   bool compress, const char *password)
{
	BakFileHeader header;
	BakFileWriter *writer;
	StringInfo	metadata_sql;
	StringInfo	schema_sql;
	TableId	   *tables;
	uint32		table_count = 0;
	BaseEntryDigest *base_digests = NULL;
	uint32		base_count = 0;
	uint64		base_stop_lsn = 0;
	char		base_db_name[NAMEDATALEN];
	XLogRecPtr	start_lsn;
	FILE	   *probe;
	ChangedTable *changed = NULL;
	uint32		changed_count = 0;

	if (!superuser())
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
				 errmsg("must be superuser to perform backups")));

	if (base_filepath == NULL || base_filepath[0] == '\0')
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("base_filepath required for SIMPLE differential backup")));

	base_db_name[0] = '\0';
	base_digests = read_base_full_digests(base_filepath, password,
										   &base_count, &base_stop_lsn,
										   base_db_name, sizeof(base_db_name));

	if (base_db_name[0] != '\0' && strcmp(base_db_name, db_name) != 0)
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("base .bak db_name \"%s\" does not match \"%s\"",
						base_db_name, db_name)));

	probe = AllocateFile(filepath, "wb");
	if (probe == NULL)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not open backup destination \"%s\": %m",
						filepath)));
	FreeFile(probe);
	(void) unlink(filepath);

	LockSharedObject(DatabaseRelationId, db_oid, 0, AccessShareLock);

	start_lsn = current_lsn();

	memset(&header, 0, sizeof(header));
	header.format_version = BAKFILE_VERSION;
	header.mode = BACKUP_MODE_SIMPLE;
	header.type = BACKUP_TYPE_DIFFERENTIAL;
	strlcpy(header.db_name, db_name, sizeof(header.db_name));
	header.db_oid = db_oid;
	header.start_lsn = (uint64) start_lsn;
	header.stop_lsn = (uint64) start_lsn;
	header.start_tli = current_tli();
	header.stop_tli = header.start_tli;
	header.pg_version = PG_VERSION_NUM;
	header.created_at = GetCurrentTimestamp();
	header.base_backup_lsn = base_stop_lsn;
	header.compressed = compress;
	header.encrypted = (password != NULL);
	header.file_count = 0;

	metadata_sql = metadata_gen_all(db_oid);
	schema_sql = ddl_gen_all(db_oid);

	SPI_connect();
	tables = collect_user_tables(&table_count);

	if (table_count > 0)
		changed = palloc0(sizeof(ChangedTable) * table_count);

	for (uint32 i = 0; i < table_count; i++)
	{
		char	   *copy_buf = NULL;
		size_t		copy_len = 0;
		char	   *entry_path;
		uint8		cur_digest[32];
		const BaseEntryDigest *base_entry;

		CHECK_FOR_INTERRUPTS();

		copy_table_to_buffer(tables[i].schema, tables[i].relname,
							 &copy_buf, &copy_len);

		sha256_buffer(copy_buf, copy_len, cur_digest);

		entry_path = psprintf("%s.%s", tables[i].schema, tables[i].relname);
		base_entry = find_base_entry(base_digests, base_count, entry_path);

		if (base_entry != NULL &&
			memcmp(base_entry->digest, cur_digest, 32) == 0)
		{
			ereport(NOTICE,
					(errmsg("skipping unchanged table %s.%s",
							tables[i].schema, tables[i].relname)));
			pfree(entry_path);
			if (copy_buf)
				pfree(copy_buf);
			continue;
		}

		ereport(NOTICE,
				(errmsg("including changed table %s.%s%s",
						tables[i].schema, tables[i].relname,
						base_entry == NULL ? " (new)" : "")));

		changed[changed_count].entry_path = entry_path;
		changed[changed_count].copy_buf = copy_buf;
		changed[changed_count].copy_len = copy_len;
		changed_count++;
	}

	header.file_count = changed_count;

	writer = bakfile_create(filepath, &header, compress, password);

	bakfile_begin_section(writer, BAKSECTION_METADATA);
	if (metadata_sql && metadata_sql->len > 0)
		bakfile_write_section_data(writer, metadata_sql->data,
								   metadata_sql->len);
	bakfile_end_section(writer);

	bakfile_begin_section(writer, BAKSECTION_SCHEMA);
	if (schema_sql && schema_sql->len > 0)
		bakfile_write_section_data(writer, schema_sql->data,
								   schema_sql->len);
	bakfile_end_section(writer);

	bakfile_begin_section(writer, BAKSECTION_DATA);
	{
		uint32		net_count = pg_hton32(changed_count);

		bakfile_write_section_data(writer, &net_count, sizeof(net_count));
	}

	for (uint32 i = 0; i < changed_count; i++)
	{
		bakfile_begin_data_entry(writer, changed[i].entry_path,
								  (uint64) changed[i].copy_len);
		if (changed[i].copy_len > 0)
			bakfile_write_data_entry_chunk(writer, changed[i].copy_buf,
										   changed[i].copy_len);
		bakfile_end_data_entry(writer);

		pfree(changed[i].entry_path);
		if (changed[i].copy_buf)
			pfree(changed[i].copy_buf);
	}

	bakfile_end_section(writer);

	bakfile_close(writer);

	SPI_finish();

	UnlockSharedObject(DatabaseRelationId, db_oid, 0, AccessShareLock);

	if (changed)
		pfree(changed);

	if (base_digests)
	{
		uint32		i;

		for (i = 0; i < base_count; i++)
			pfree(base_digests[i].path);
		pfree(base_digests);
	}

	ereport(NOTICE,
			(errmsg("SIMPLE DIFFERENTIAL backup of \"%s\" complete: %u of %u tables changed, written to \"%s\"",
					db_name, changed_count, table_count, filepath)));
}
