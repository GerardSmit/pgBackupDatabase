#include "postgres.h"

#include <sys/stat.h>

#include "common/cryptohash.h"
#include "common/file_perm.h"
#include "common/jsonapi.h"
#include "lib/stringinfo.h"
#include "miscadmin.h"
#include "port/pg_bswap.h"
#include "storage/fd.h"
#include "utils/builtins.h"
#include "utils/json.h"
#include "utils/jsonb.h"
#include "utils/numeric.h"
#include "utils/timestamp.h"

#include "bakfile.h"
#include "bakfile_crypto.h"

static void
bakfile_write_raw(BakFileWriter *writer, const void *data, size_t len)
{
	if (fwrite(data, 1, len, writer->fp) != len)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not write to backup file \"%s\": %m",
						writer->temp_filepath)));
}

static void
bakfile_read_raw(BakFileReader *reader, void *buf, size_t len)
{
	if (fread(buf, 1, len, reader->fp) != len)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not read from backup file \"%s\": %m",
						reader->filepath)));
}

static void
bakfile_write_uint16(BakFileWriter *writer, uint16 val)
{
	uint16		netval = pg_hton16(val);

	bakfile_write_raw(writer, &netval, sizeof(netval));
}

static void
bakfile_write_uint32(BakFileWriter *writer, uint32 val)
{
	uint32		netval = pg_hton32(val);

	bakfile_write_raw(writer, &netval, sizeof(netval));
}

static uint16
bakfile_read_uint16(BakFileReader *reader)
{
	uint16		netval;

	bakfile_read_raw(reader, &netval, sizeof(netval));
	return pg_ntoh16(netval);
}

static uint32
bakfile_read_uint32(BakFileReader *reader)
{
	uint32		netval;

	bakfile_read_raw(reader, &netval, sizeof(netval));
	return pg_ntoh32(netval);
}

static uint64
bakfile_read_uint64(BakFileReader *reader)
{
	uint64		netval;

	bakfile_read_raw(reader, &netval, sizeof(netval));
	return pg_ntoh64(netval);
}

static char *
jsonb_lookup_text(Jsonb *jb, const char *key)
{
	JsonbValue	keyv;
	JsonbValue *resv;

	keyv.type = jbvString;
	keyv.val.string.val = (char *) key;
	keyv.val.string.len = strlen(key);

	resv = findJsonbValueFromContainer(&jb->root, JB_FOBJECT, &keyv);
	if (resv == NULL || resv->type == jbvNull)
		return NULL;
	if (resv->type != jbvString)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("backup header field \"%s\" is not a string", key)));
	return pnstrdup(resv->val.string.val, resv->val.string.len);
}

static bool
jsonb_lookup_bool(Jsonb *jb, const char *key, bool dflt)
{
	JsonbValue	keyv;
	JsonbValue *resv;

	keyv.type = jbvString;
	keyv.val.string.val = (char *) key;
	keyv.val.string.len = strlen(key);

	resv = findJsonbValueFromContainer(&jb->root, JB_FOBJECT, &keyv);
	if (resv == NULL || resv->type == jbvNull)
		return dflt;
	if (resv->type != jbvBool)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("backup header field \"%s\" is not a boolean", key)));
	return resv->val.boolean;
}

static int64
jsonb_lookup_int64(Jsonb *jb, const char *key, int64 dflt)
{
	JsonbValue	keyv;
	JsonbValue *resv;
	Datum		num_datum;
	char	   *str;

	keyv.type = jbvString;
	keyv.val.string.val = (char *) key;
	keyv.val.string.len = strlen(key);

	resv = findJsonbValueFromContainer(&jb->root, JB_FOBJECT, &keyv);
	if (resv == NULL || resv->type == jbvNull)
		return dflt;
	if (resv->type != jbvNumeric)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("backup header field \"%s\" is not numeric", key)));

	num_datum = DirectFunctionCall1(numeric_out,
									 NumericGetDatum(resv->val.numeric));
	str = DatumGetCString(num_datum);
	return strtoll(str, NULL, 10);
}

static uint64
parse_lsn_text(const char *s)
{
	uint32		hi;
	uint32		lo;

	if (s == NULL)
		return 0;
	if (sscanf(s, "%X/%X", &hi, &lo) != 2)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("invalid LSN format in backup header: \"%s\"", s)));
	return ((uint64) hi << 32) | lo;
}

static void
bakfile_header_from_json(const char *json_text, BakFileHeader *hdr)
{
	Datum		jb_datum;
	Jsonb	   *jb;
	char	   *s;

	jb_datum = DirectFunctionCall1(jsonb_in, CStringGetDatum(json_text));
	jb = DatumGetJsonbP(jb_datum);

	hdr->format_version = (uint16) jsonb_lookup_int64(jb, "format_version", 0);

	s = jsonb_lookup_text(jb, "mode");
	hdr->mode = (s && strcmp(s, "full") == 0) ? BACKUP_MODE_FULL
											  : BACKUP_MODE_SIMPLE;

	s = jsonb_lookup_text(jb, "type");
	if (s == NULL || strcmp(s, "full") == 0)
		hdr->type = BACKUP_TYPE_FULL;
	else if (strcmp(s, "differential") == 0)
		hdr->type = BACKUP_TYPE_DIFFERENTIAL;
	else if (strcmp(s, "log") == 0)
		hdr->type = BACKUP_TYPE_LOG;
	else
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("unknown backup type \"%s\"", s)));

	s = jsonb_lookup_text(jb, "db_name");
	if (s)
		strlcpy(hdr->db_name, s, sizeof(hdr->db_name));

	hdr->db_oid = (Oid) jsonb_lookup_int64(jb, "db_oid", 0);
	hdr->start_lsn = parse_lsn_text(jsonb_lookup_text(jb, "start_lsn"));
	hdr->stop_lsn = parse_lsn_text(jsonb_lookup_text(jb, "stop_lsn"));
	s = jsonb_lookup_text(jb, "start_tli");
	hdr->start_tli = (s != NULL) ? (uint32) strtoul(s, NULL, 10) : 0;
	s = jsonb_lookup_text(jb, "stop_tli");
	hdr->stop_tli = (s != NULL) ? (uint32) strtoul(s, NULL, 10) : 0;
	hdr->pg_version = (uint32) jsonb_lookup_int64(jb, "pg_version", 0);
	hdr->created_at = jsonb_lookup_int64(jb, "created_at", 0);
	hdr->base_backup_lsn = parse_lsn_text(jsonb_lookup_text(jb, "base_backup_lsn"));
	hdr->compressed = jsonb_lookup_bool(jb, "compressed", false);

	s = jsonb_lookup_text(jb, "compression_algo");
	if (s)
		strlcpy(hdr->compression_algo, s, sizeof(hdr->compression_algo));

	hdr->encrypted = jsonb_lookup_bool(jb, "encrypted", false);

	s = jsonb_lookup_text(jb, "encryption_algo");
	if (s)
		strlcpy(hdr->encryption_algo, s, sizeof(hdr->encryption_algo));

	if (hdr->encrypted)
	{
		char	   *hex_salt = jsonb_lookup_text(jb, "key_salt");
		char	   *hex_iv = jsonb_lookup_text(jb, "key_iv");
		int			i;

		if (hex_salt == NULL || strlen(hex_salt) != 32)
			ereport(ERROR,
					(errcode(ERRCODE_DATA_CORRUPTED),
					 errmsg("encrypted backup header missing or invalid key_salt")));
		if (hex_iv == NULL || strlen(hex_iv) != 24)
			ereport(ERROR,
					(errcode(ERRCODE_DATA_CORRUPTED),
					 errmsg("encrypted backup header missing or invalid key_iv")));
		for (i = 0; i < 16; i++)
		{
			unsigned int b;

			if (sscanf(hex_salt + i * 2, "%2x", &b) != 1)
				ereport(ERROR,
						(errcode(ERRCODE_DATA_CORRUPTED),
						 errmsg("invalid hex in key_salt")));
			hdr->key_salt[i] = (uint8) b;
		}
		for (i = 0; i < 12; i++)
		{
			unsigned int b;

			if (sscanf(hex_iv + i * 2, "%2x", &b) != 1)
				ereport(ERROR,
						(errcode(ERRCODE_DATA_CORRUPTED),
						 errmsg("invalid hex in key_iv")));
			hdr->key_iv[i] = (uint8) b;
		}
	}

	hdr->file_count = (uint32) jsonb_lookup_int64(jb, "file_count", 0);
	s = jsonb_lookup_text(jb, "frozen_xid");
	hdr->frozen_xid = (s != NULL) ? (uint32) strtoul(s, NULL, 10) : 0;
	s = jsonb_lookup_text(jb, "min_mxid");
	hdr->min_mxid = (s != NULL) ? (uint32) strtoul(s, NULL, 10) : 0;
	hdr->checkpoint_lsn = parse_lsn_text(jsonb_lookup_text(jb, "checkpoint_lsn"));
}

static StringInfo
bakfile_header_to_json(BakFileHeader *hdr)
{
	StringInfo	buf = makeStringInfo();

	appendStringInfoChar(buf, '{');

	appendStringInfo(buf, "\"format_version\":%u", hdr->format_version);
	appendStringInfo(buf, ",\"mode\":\"%s\"",
					 hdr->mode == BACKUP_MODE_FULL ? "full" : "simple");

	{
		const char *type_str;

		switch (hdr->type)
		{
			case BACKUP_TYPE_FULL:
				type_str = "full";
				break;
			case BACKUP_TYPE_DIFFERENTIAL:
				type_str = "differential";
				break;
			case BACKUP_TYPE_LOG:
				type_str = "log";
				break;
			default:
				type_str = "unknown";
		}
		appendStringInfo(buf, ",\"type\":\"%s\"", type_str);
	}

	appendStringInfo(buf, ",\"db_name\":\"%s\"", hdr->db_name);
	appendStringInfo(buf, ",\"db_oid\":%u", hdr->db_oid);
	appendStringInfo(buf, ",\"start_lsn\":\"%08X/%08X\"",
					 (uint32) (hdr->start_lsn >> 32),
					 (uint32) hdr->start_lsn);
	appendStringInfo(buf, ",\"stop_lsn\":\"%08X/%08X\"",
					 (uint32) (hdr->stop_lsn >> 32),
					 (uint32) hdr->stop_lsn);
	appendStringInfo(buf, ",\"start_tli\":\"%010u\"", hdr->start_tli);
	appendStringInfo(buf, ",\"stop_tli\":\"%010u\"", hdr->stop_tli);
	appendStringInfo(buf, ",\"pg_version\":%u", hdr->pg_version);
	appendStringInfo(buf, ",\"created_at\":" INT64_FORMAT,
					 hdr->created_at);
	appendStringInfo(buf, ",\"base_backup_lsn\":\"%08X/%08X\"",
					 (uint32) (hdr->base_backup_lsn >> 32),
					 (uint32) hdr->base_backup_lsn);
	appendStringInfo(buf, ",\"compressed\":%s",
					 hdr->compressed ? "true" : "false");
	appendStringInfo(buf, ",\"compression_algo\":\"%s\"",
					 hdr->compression_algo);
	appendStringInfo(buf, ",\"encrypted\":%s",
					 hdr->encrypted ? "true" : "false");
	appendStringInfo(buf, ",\"encryption_algo\":\"%s\"",
					 hdr->encryption_algo);

	if (hdr->encrypted)
	{
		char		hex_salt[33];
		char		hex_iv[25];
		int			i;

		for (i = 0; i < 16; i++)
			snprintf(hex_salt + i * 2, 3, "%02x", hdr->key_salt[i]);
		for (i = 0; i < 12; i++)
			snprintf(hex_iv + i * 2, 3, "%02x", hdr->key_iv[i]);
		appendStringInfo(buf, ",\"key_salt\":\"%s\"", hex_salt);
		appendStringInfo(buf, ",\"key_iv\":\"%s\"", hex_iv);
	}

	appendStringInfo(buf, ",\"file_count\":%u", hdr->file_count);
	appendStringInfo(buf, ",\"frozen_xid\":\"%010u\"", hdr->frozen_xid);
	appendStringInfo(buf, ",\"min_mxid\":\"%010u\"", hdr->min_mxid);
	appendStringInfo(buf, ",\"checkpoint_lsn\":\"%08X/%08X\"",
					 (uint32) (hdr->checkpoint_lsn >> 32),
					 (uint32) hdr->checkpoint_lsn);

	appendStringInfoChar(buf, '}');
	return buf;
}

BakFileWriter *
bakfile_create(const char *filepath, BakFileHeader *header,
			   bool compress, const char *password)
{
	BakFileWriter *writer;
	StringInfo	header_json;

	writer = palloc0(sizeof(BakFileWriter));
	writer->filepath = pstrdup(filepath);
	writer->temp_filepath = psprintf("%s.tmp", filepath);
	writer->compress = compress;
	writer->password = password ? pstrdup(password) : NULL;
	memcpy(&writer->header, header, sizeof(BakFileHeader));

	writer->header.format_version = BAKFILE_VERSION;
	writer->header.compressed = compress;
	if (compress)
		strlcpy(writer->header.compression_algo, "zstd",
				sizeof(writer->header.compression_algo));
	writer->header.encrypted = (password != NULL);
	if (password)
		strlcpy(writer->header.encryption_algo, "aes-256-gcm",
				sizeof(writer->header.encryption_algo));

	writer->fp = AllocateFile(writer->temp_filepath, "wb+");
	if (writer->fp == NULL)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not create backup file \"%s\": %m",
						writer->temp_filepath)));

	if (password != NULL)
	{
		BakCryptoContext *throwaway =
			bakcrypto_writer_init(false, password,
								  writer->header.key_salt,
								  writer->header.key_iv);
		bakcrypto_free(throwaway);
	}

	/* Write file magic */
	bakfile_write_raw(writer, BAKFILE_MAGIC, BAKFILE_MAGIC_LEN);

	/* Write format version */
	bakfile_write_uint16(writer, BAKFILE_VERSION);

	/* Write header JSON */
	header_json = bakfile_header_to_json(&writer->header);
	bakfile_write_uint32(writer, header_json->len);
	bakfile_write_raw(writer, header_json->data, header_json->len);
	pfree(header_json->data);
	pfree(header_json);

	return writer;
}

void
bakfile_begin_section(BakFileWriter *writer, uint8 section_type)
{
	if (writer->section_open)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("cannot begin section: previous section still open")));

	bakfile_write_raw(writer, &section_type, 1);

	writer->section_length_offset = ftello(writer->fp);
	if (writer->section_length_offset < 0)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not get position in backup file \"%s\": %m",
						writer->filepath)));

	{
		uint64	zero = 0;

		bakfile_write_raw(writer, &zero, sizeof(zero));
	}

	writer->section_bytes = 0;
	writer->section_direct = !(writer->compress || writer->password != NULL);
	writer->section_buf = writer->section_direct ? NULL : makeStringInfo();
	writer->section_open = true;
}

void
bakfile_write_section_data(BakFileWriter *writer, const void *data, size_t len)
{
	if (!writer->section_open)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("no section open")));

	if (writer->section_direct)
	{
		bakfile_write_raw(writer, data, len);
		writer->section_bytes += len;
	}
	else
		appendBinaryStringInfo(writer->section_buf, (const char *) data, len);
}

void
bakfile_end_section(BakFileWriter *writer)
{
	off_t		end_pos;
	uint64		netlen;
	void	   *out_blob = NULL;
	size_t		out_len;
	BakCryptoContext *cctx;
	bool		do_crypto;

	if (!writer->section_open)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("no section open")));

	do_crypto = writer->compress || (writer->password != NULL);

	if (do_crypto)
	{
		cctx = bakcrypto_reader_init(writer->compress,
									 writer->password != NULL,
									 writer->password,
									 writer->password ? writer->header.key_salt : NULL,
									 writer->password ? writer->header.key_iv : NULL);
		out_len = bakcrypto_process(cctx,
									writer->section_buf->data,
									writer->section_buf->len,
									&out_blob);
		bakcrypto_free(cctx);

		bakfile_write_raw(writer, out_blob, out_len);
		writer->section_bytes = out_len;
		pfree(out_blob);
	}
	else
	{
		if (!writer->section_direct)
		{
			bakfile_write_raw(writer, writer->section_buf->data,
							  writer->section_buf->len);
			writer->section_bytes = writer->section_buf->len;
		}
	}

	end_pos = ftello(writer->fp);
	if (end_pos < 0)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not get position in backup file \"%s\": %m",
						writer->filepath)));

	if (fseeko(writer->fp, writer->section_length_offset, SEEK_SET) != 0)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not seek in backup file \"%s\": %m",
						writer->filepath)));

	netlen = pg_hton64(writer->section_bytes);
	if (fwrite(&netlen, 1, sizeof(netlen), writer->fp) != sizeof(netlen))
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not backfill section length in \"%s\": %m",
						writer->filepath)));

	if (fseeko(writer->fp, end_pos, SEEK_SET) != 0)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not seek in backup file \"%s\": %m",
						writer->filepath)));

	if (writer->section_buf)
	{
		pfree(writer->section_buf->data);
		pfree(writer->section_buf);
	}
	writer->section_buf = NULL;
	writer->section_direct = false;
	writer->section_open = false;
}

static void
section_buf_append_uint16(BakFileWriter *writer, uint16 val)
{
	uint16		netval = pg_hton16(val);

	bakfile_write_section_data(writer, &netval, sizeof(netval));
}

static void
section_buf_append_uint64(BakFileWriter *writer, uint64 val)
{
	uint64		netval = pg_hton64(val);

	bakfile_write_section_data(writer, &netval, sizeof(netval));
}

void
bakfile_begin_data_entry(BakFileWriter *writer, const char *path,
						  uint64 data_len)
{
	uint16		path_len = strlen(path);

	if (writer->entry_open)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("cannot begin data entry: previous entry still open")));
	if (!writer->section_open)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("no section open for data entry")));

	section_buf_append_uint16(writer, path_len);
	bakfile_write_section_data(writer, path, path_len);
	section_buf_append_uint64(writer, data_len);

	writer->entry_hash_ctx = pg_cryptohash_create(PG_SHA256);
	if (writer->entry_hash_ctx == NULL)
		ereport(ERROR,
				(errcode(ERRCODE_OUT_OF_MEMORY),
				 errmsg("could not create SHA-256 context")));
	if (pg_cryptohash_init(writer->entry_hash_ctx) < 0)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not initialize SHA-256 context")));
	writer->entry_open = true;
}

void
bakfile_write_data_entry_chunk(BakFileWriter *writer, const void *data,
								size_t len)
{
	if (!writer->entry_open)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("no data entry open")));

	bakfile_write_section_data(writer, data, len);

	if (pg_cryptohash_update(writer->entry_hash_ctx, data, len) < 0)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not update SHA-256 context")));
}

void
bakfile_end_data_entry(BakFileWriter *writer)
{
	uint8		digest[32];

	if (!writer->entry_open)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("no data entry open")));

	if (pg_cryptohash_final(writer->entry_hash_ctx, digest, sizeof(digest)) < 0)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not finalize SHA-256 context")));

	bakfile_write_section_data(writer, digest, sizeof(digest));

	pg_cryptohash_free(writer->entry_hash_ctx);
	writer->entry_hash_ctx = NULL;
	writer->entry_open = false;
}

static void
bakfile_compute_file_sha256(BakFileWriter *writer, uint8 *digest_out)
{
	pg_cryptohash_ctx *ctx;
	uint8		buf[8192];
	size_t		n;

	if (fflush(writer->fp) != 0)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not flush backup file \"%s\": %m",
						writer->filepath)));

	if (fseeko(writer->fp, 0, SEEK_SET) != 0)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not seek in backup file \"%s\": %m",
						writer->filepath)));

	ctx = pg_cryptohash_create(PG_SHA256);
	if (ctx == NULL || pg_cryptohash_init(ctx) < 0)
	{
		if (ctx)
			pg_cryptohash_free(ctx);
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not init file SHA-256 context")));
	}

	/*
	 * pg_cryptohash_create allocates a palloc'd struct that holds an
	 * OpenSSL EVP_MD_CTX. MemoryContext reset on xact abort frees the
	 * outer struct but not the OpenSSL handle; release explicitly along
	 * every error path before re-throwing.
	 */
	PG_TRY();
	{
		while ((n = fread(buf, 1, sizeof(buf), writer->fp)) > 0)
		{
			if (pg_cryptohash_update(ctx, buf, n) < 0)
				ereport(ERROR,
						(errcode(ERRCODE_INTERNAL_ERROR),
						 errmsg("could not update file SHA-256 context")));
		}

		if (ferror(writer->fp))
			ereport(ERROR,
					(errcode_for_file_access(),
					 errmsg("could not read back backup file \"%s\": %m",
							writer->filepath)));

		if (pg_cryptohash_final(ctx, digest_out, 32) < 0)
			ereport(ERROR,
					(errcode(ERRCODE_INTERNAL_ERROR),
					 errmsg("could not finalize file SHA-256 context")));
	}
	PG_CATCH();
	{
		pg_cryptohash_free(ctx);
		PG_RE_THROW();
	}
	PG_END_TRY();

	pg_cryptohash_free(ctx);

	if (fseeko(writer->fp, 0, SEEK_END) != 0)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not seek to end of \"%s\": %m",
						writer->filepath)));
}

void
bakfile_close(BakFileWriter *writer)
{
	uint8		file_digest[32];

	bakfile_compute_file_sha256(writer, file_digest);

	bakfile_write_raw(writer, file_digest, sizeof(file_digest));
	bakfile_write_raw(writer, BAKFILE_MAGIC, BAKFILE_MAGIC_LEN);

	if (fflush(writer->fp) != 0)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not flush backup file \"%s\": %m",
						writer->temp_filepath)));
	if (pg_fsync(fileno(writer->fp)) != 0)
		ereport(data_sync_elevel(ERROR),
				(errcode_for_file_access(),
				 errmsg("could not fsync backup file \"%s\": %m",
						writer->temp_filepath)));

	FreeFile(writer->fp);
	writer->fp = NULL;

	durable_rename(writer->temp_filepath, writer->filepath, ERROR);
}

void
bakfile_rewrite_header(BakFileWriter *writer, BakFileHeader *header)
{
	StringInfo	new_json;
	off_t		saved_pos;
	off_t		header_offset =
		BAKFILE_MAGIC_LEN + sizeof(uint16) + sizeof(uint32);

	memcpy(&writer->header, header, sizeof(BakFileHeader));
	writer->header.format_version = BAKFILE_VERSION;
	writer->header.compressed = writer->compress;
	if (writer->compress)
		strlcpy(writer->header.compression_algo, "zstd",
				sizeof(writer->header.compression_algo));
	writer->header.encrypted = (writer->password != NULL);
	if (writer->password)
		strlcpy(writer->header.encryption_algo, "aes-256-gcm",
				sizeof(writer->header.encryption_algo));

	new_json = bakfile_header_to_json(&writer->header);

	saved_pos = ftello(writer->fp);
	if (saved_pos < 0)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not get position in backup file \"%s\": %m",
						writer->filepath)));

	if (fseeko(writer->fp, header_offset, SEEK_SET) != 0)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not seek to header offset in \"%s\": %m",
						writer->filepath)));

	bakfile_write_raw(writer, new_json->data, new_json->len);

	if (fseeko(writer->fp, saved_pos, SEEK_SET) != 0)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not restore position in \"%s\": %m",
						writer->filepath)));

	pfree(new_json->data);
	pfree(new_json);
}

BakFileReader *
bakfile_open(const char *filepath, const char *password)
{
	BakFileReader *reader;
	char		magic_buf[BAKFILE_MAGIC_LEN];
	uint16		version;
	uint32		header_len;
	char	   *header_json;
	char		tail_buf[BAKFILE_MAGIC_LEN];
	off_t		fsize;

	reader = palloc0(sizeof(BakFileReader));
	reader->filepath = pstrdup(filepath);
	reader->password = password ? pstrdup(password) : NULL;

	reader->fp = AllocateFile(filepath, "rb");
	if (reader->fp == NULL)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not open backup file \"%s\": %m", filepath)));

	/* Verify tail magic exists at expected offset (catches truncation). */
	if (fseeko(reader->fp, 0, SEEK_END) != 0)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not seek to end of \"%s\": %m", filepath)));
	fsize = ftello(reader->fp);
	if (fsize < (off_t) (BAKFILE_MAGIC_LEN + 32 + BAKFILE_MAGIC_LEN))
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("\"%s\" is too small to be a valid .bak", filepath)));
	if (fseeko(reader->fp, fsize - BAKFILE_MAGIC_LEN, SEEK_SET) != 0)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not seek to tail magic in \"%s\": %m", filepath)));
	if (fread(tail_buf, 1, BAKFILE_MAGIC_LEN, reader->fp) != BAKFILE_MAGIC_LEN ||
		memcmp(tail_buf, BAKFILE_MAGIC, BAKFILE_MAGIC_LEN) != 0)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("\"%s\" is truncated or corrupted (missing tail magic)",
						filepath)));
	if (fseeko(reader->fp, 0, SEEK_SET) != 0)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not rewind \"%s\": %m", filepath)));

	/* Read and verify magic */
	bakfile_read_raw(reader, magic_buf, BAKFILE_MAGIC_LEN);
	if (memcmp(magic_buf, BAKFILE_MAGIC, BAKFILE_MAGIC_LEN) != 0)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("\"%s\" is not a valid .bak file", filepath)));

	/* Read format version */
	version = bakfile_read_uint16(reader);
	if (version > BAKFILE_VERSION)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("unsupported .bak format version %u (max supported: %u)",
						version, BAKFILE_VERSION)));

	reader->header.format_version = version;

	header_len = bakfile_read_uint32(reader);
	header_json = palloc(header_len + 1);
	bakfile_read_raw(reader, header_json, header_len);
	header_json[header_len] = '\0';

	bakfile_header_from_json(header_json, &reader->header);

	pfree(header_json);
	return reader;
}

static void
section_plain_read(BakFileReader *reader, void *buf, size_t len)
{
	if (reader->section_plain_pos + len > reader->section_plain_len)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("attempt to read past end of section in \"%s\"",
						reader->filepath)));

	if (reader->section_direct)
		bakfile_read_raw(reader, buf, len);
	else
		memcpy(buf, reader->section_plain + reader->section_plain_pos, len);

	reader->section_plain_pos += len;
}

static uint16
section_plain_read_uint16(BakFileReader *reader)
{
	uint16		netval;

	section_plain_read(reader, &netval, sizeof(netval));
	return pg_ntoh16(netval);
}

static uint32
section_plain_read_uint32(BakFileReader *reader)
{
	uint32		netval;

	section_plain_read(reader, &netval, sizeof(netval));
	return pg_ntoh32(netval);
}

static uint64
section_plain_read_uint64(BakFileReader *reader)
{
	uint64		netval;

	section_plain_read(reader, &netval, sizeof(netval));
	return pg_ntoh64(netval);
}

uint8
bakfile_next_section(BakFileReader *reader)
{
	uint8		section_type;
	uint64		section_length;
	char	   *raw;
	BakCryptoContext *cctx;
	bool		do_crypto;
	off_t		cur_pos;
	off_t		end_pos;

	if (reader->section_direct)
	{
		if (fseeko(reader->fp, reader->section_end_offset, SEEK_SET) != 0)
			ereport(ERROR,
					(errcode_for_file_access(),
					 errmsg("could not skip section in \"%s\": %m",
							reader->filepath)));
		reader->section_direct = false;
	}

	if (reader->section_plain)
	{
		pfree(reader->section_plain);
		reader->section_plain = NULL;
	}
	reader->section_plain_len = 0;
	reader->section_plain_pos = 0;
	reader->section_end_offset = 0;

	/*
	 * Detect EOF before the footer (last 32+5 bytes = SHA-256 + tail magic):
	 * if we're at or past the section-stream end, signal EOF to caller.
	 */
	cur_pos = ftello(reader->fp);
	if (fseeko(reader->fp, 0, SEEK_END) == 0)
	{
		end_pos = ftello(reader->fp);
		if (fseeko(reader->fp, cur_pos, SEEK_SET) != 0)
			ereport(ERROR,
					(errcode_for_file_access(),
					 errmsg("could not restore file position in \"%s\": %m",
							reader->filepath)));
		if (cur_pos >= end_pos - 32 - BAKFILE_MAGIC_LEN)
		{
			reader->current_section_type = 0;
			return 0;
		}
	}

	bakfile_read_raw(reader, &section_type, 1);
	section_length = bakfile_read_uint64(reader);

	reader->current_section_type = section_type;
	reader->current_section_length = section_length;

	do_crypto = reader->header.compressed || reader->header.encrypted;

	if (do_crypto)
	{
		void	   *plain = NULL;
		size_t		plain_len;

		raw = palloc(section_length == 0 ? 1 : (size_t) section_length);
		if (section_length > 0)
			bakfile_read_raw(reader, raw, (size_t) section_length);

		cctx = bakcrypto_reader_init(reader->header.compressed,
									 reader->header.encrypted,
									 reader->password,
									 reader->header.encrypted ? reader->header.key_salt : NULL,
									 reader->header.encrypted ? reader->header.key_iv : NULL);
		plain_len = bakcrypto_unprocess(cctx, raw, (size_t) section_length,
										&plain);
		bakcrypto_free(cctx);
		pfree(raw);

		reader->section_plain = plain;
		reader->section_plain_len = plain_len;
	}
	else
	{
		cur_pos = ftello(reader->fp);
		if (cur_pos < 0)
			ereport(ERROR,
					(errcode_for_file_access(),
					 errmsg("could not get section position in \"%s\": %m",
							reader->filepath)));
		reader->section_plain = NULL;
		reader->section_plain_len = (size_t) section_length;
		reader->section_direct = true;
		reader->section_end_offset = cur_pos + (off_t) section_length;
	}

	return section_type;
}

size_t
bakfile_read_section_data(BakFileReader *reader, void *buf, size_t len)
{
	section_plain_read(reader, buf, len);
	return len;
}

bool
bakfile_next_data_entry(BakFileReader *reader, BakDataEntry *entry)
{
	if (reader->section_plain_pos >= reader->section_plain_len)
		return false;

	entry->path_len = section_plain_read_uint16(reader);
	entry->path = palloc(entry->path_len + 1);
	section_plain_read(reader, entry->path, entry->path_len);
	entry->path[entry->path_len] = '\0';
	entry->data_len = section_plain_read_uint64(reader);
	return true;
}

size_t
bakfile_read_data_entry_chunk(BakFileReader *reader, void *buf, size_t len)
{
	section_plain_read(reader, buf, len);
	return len;
}

void
bakfile_finish_data_entry(BakFileReader *reader, BakDataEntry *entry,
						   const void *data, bool verify)
{
	uint8		stored[32];

	section_plain_read(reader, stored, sizeof(stored));
	memcpy(entry->checksum, stored, sizeof(stored));

	if (verify && data != NULL)
	{
		pg_cryptohash_ctx *ctx;
		uint8		computed[32];

		ctx = pg_cryptohash_create(PG_SHA256);
		if (ctx == NULL || pg_cryptohash_init(ctx) < 0)
			ereport(ERROR,
					(errcode(ERRCODE_INTERNAL_ERROR),
					 errmsg("could not init SHA-256 for entry verify")));
		if (entry->data_len > 0
			&& pg_cryptohash_update(ctx, data, (size_t) entry->data_len) < 0)
			ereport(ERROR,
					(errcode(ERRCODE_INTERNAL_ERROR),
					 errmsg("could not update SHA-256 for entry verify")));
		if (pg_cryptohash_final(ctx, computed, sizeof(computed)) < 0)
			ereport(ERROR,
					(errcode(ERRCODE_INTERNAL_ERROR),
					 errmsg("could not finalize SHA-256 for entry verify")));
		pg_cryptohash_free(ctx);

		if (memcmp(stored, computed, sizeof(stored)) != 0)
			ereport(ERROR,
					(errcode(ERRCODE_DATA_CORRUPTED),
					 errmsg("data entry SHA-256 mismatch for \"%s\"",
							entry->path)));
	}
}

uint32
bakfile_read_data_entry_count(BakFileReader *reader)
{
	return section_plain_read_uint32(reader);
}

BakDataEntryMeta *
bakfile_list_data_entries(BakFileReader *reader, uint32 *count_out)
{
	uint32		count;
	BakDataEntryMeta *list;
	uint32		i;

	count = section_plain_read_uint32(reader);
	*count_out = count;

	if (count == 0)
		return NULL;

	list = palloc0(sizeof(BakDataEntryMeta) * count);

	for (i = 0; i < count; i++)
	{
		uint16		path_len;
		uint64		data_len;

		path_len = section_plain_read_uint16(reader);
		list[i].path = palloc(path_len + 1);
		section_plain_read(reader, list[i].path, path_len);
		list[i].path[path_len] = '\0';

		data_len = section_plain_read_uint64(reader);
		list[i].data_len = data_len;

		if (reader->section_plain_pos + data_len > reader->section_plain_len)
			ereport(ERROR,
					(errcode(ERRCODE_DATA_CORRUPTED),
					 errmsg("DATA entry %u extends past section in \"%s\"",
							i, reader->filepath)));

		if (reader->section_direct &&
			fseeko(reader->fp, (off_t) data_len, SEEK_CUR) != 0)
			ereport(ERROR,
					(errcode_for_file_access(),
					 errmsg("could not skip DATA entry in \"%s\": %m",
							reader->filepath)));
		reader->section_plain_pos += (size_t) data_len;

		section_plain_read(reader, list[i].digest, sizeof(list[i].digest));
	}

	return list;
}

uint64
bakfile_section_remaining(BakFileReader *reader)
{
	return reader->section_plain_len - reader->section_plain_pos;
}

void
bakfile_read_section_all(BakFileReader *reader, char **buf_out, size_t *len_out)
{
	size_t		remaining = reader->section_plain_len - reader->section_plain_pos;
	char	   *out;

	out = palloc(remaining + 1);
	if (remaining > 0)
		section_plain_read(reader, out, remaining);
	out[remaining] = '\0';
	*buf_out = out;
	*len_out = remaining;
}

bool
bakfile_verify(const char *filepath, const char *password, char **detail)
{
	FILE	   *fp;
	off_t		file_size;
	off_t		hashed_len;
	char		head_magic[BAKFILE_MAGIC_LEN];
	char		tail_magic[BAKFILE_MAGIC_LEN];
	uint8		stored_digest[32];
	uint8		computed_digest[32];
	pg_cryptohash_ctx *ctx;
	uint8		buf[8192];
	size_t		remaining;
	size_t		n;

	fp = AllocateFile(filepath, "rb");
	if (fp == NULL)
	{
		*detail = psprintf("could not open \"%s\"", filepath);
		return false;
	}

	if (fseeko(fp, 0, SEEK_END) != 0 || (file_size = ftello(fp)) < 0)
	{
		FreeFile(fp);
		*detail = pstrdup("could not seek to end");
		return false;
	}

	if (file_size < (off_t) (BAKFILE_MAGIC_LEN + 2 + 4 + 32 + BAKFILE_MAGIC_LEN))
	{
		FreeFile(fp);
		*detail = pstrdup("file too small to be a valid .bak");
		return false;
	}

	if (fseeko(fp, 0, SEEK_SET) != 0
		|| fread(head_magic, 1, BAKFILE_MAGIC_LEN, fp) != BAKFILE_MAGIC_LEN
		|| memcmp(head_magic, BAKFILE_MAGIC, BAKFILE_MAGIC_LEN) != 0)
	{
		FreeFile(fp);
		*detail = pstrdup("head magic mismatch");
		return false;
	}

	if (fseeko(fp, file_size - BAKFILE_MAGIC_LEN, SEEK_SET) != 0
		|| fread(tail_magic, 1, BAKFILE_MAGIC_LEN, fp) != BAKFILE_MAGIC_LEN
		|| memcmp(tail_magic, BAKFILE_MAGIC, BAKFILE_MAGIC_LEN) != 0)
	{
		FreeFile(fp);
		*detail = pstrdup("tail magic mismatch (possible truncation)");
		return false;
	}

	if (fseeko(fp, file_size - BAKFILE_MAGIC_LEN - 32, SEEK_SET) != 0
		|| fread(stored_digest, 1, 32, fp) != 32)
	{
		FreeFile(fp);
		*detail = pstrdup("could not read stored checksum");
		return false;
	}

	hashed_len = file_size - BAKFILE_MAGIC_LEN - 32;
	if (fseeko(fp, 0, SEEK_SET) != 0)
	{
		FreeFile(fp);
		*detail = pstrdup("could not seek to start for hashing");
		return false;
	}

	ctx = pg_cryptohash_create(PG_SHA256);
	if (ctx == NULL || pg_cryptohash_init(ctx) < 0)
	{
		if (ctx)
			pg_cryptohash_free(ctx);
		FreeFile(fp);
		*detail = pstrdup("could not init SHA-256");
		return false;
	}

	remaining = (size_t) hashed_len;
	while (remaining > 0)
	{
		size_t		want = remaining < sizeof(buf) ? remaining : sizeof(buf);

		n = fread(buf, 1, want, fp);
		if (n == 0)
			break;
		if (pg_cryptohash_update(ctx, buf, n) < 0)
		{
			pg_cryptohash_free(ctx);
			FreeFile(fp);
			*detail = pstrdup("SHA-256 update failed");
			return false;
		}
		remaining -= n;
	}

	if (pg_cryptohash_final(ctx, computed_digest, sizeof(computed_digest)) < 0)
	{
		pg_cryptohash_free(ctx);
		FreeFile(fp);
		*detail = pstrdup("SHA-256 finalize failed");
		return false;
	}

	pg_cryptohash_free(ctx);
	FreeFile(fp);

	if (memcmp(stored_digest, computed_digest, 32) != 0)
	{
		*detail = pstrdup("checksum mismatch (file modified or corrupted)");
		return false;
	}

	*detail = pstrdup("ok");
	return true;
}

void
bakfile_close_reader(BakFileReader *reader)
{
	if (reader->fp)
		FreeFile(reader->fp);
	reader->fp = NULL;
	if (reader->section_plain)
	{
		pfree(reader->section_plain);
		reader->section_plain = NULL;
	}
}
