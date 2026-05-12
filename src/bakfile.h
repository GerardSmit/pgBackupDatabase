#ifndef BAKFILE_H
#define BAKFILE_H

#include "postgres.h"
#include "common/cryptohash.h"
#include "pg_dbbackup.h"

#define BAKFILE_MAGIC		"PGBAK"
#define BAKFILE_MAGIC_LEN	5
#define BAKFILE_VERSION		1

/* Section type identifiers */
#define BAKSECTION_METADATA		0x01
#define BAKSECTION_SCHEMA		0x02
#define BAKSECTION_DATA			0x03
#define BAKSECTION_LOGICAL_STREAM 0x04

/* Data entry within DATA section */
typedef struct BakDataEntry
{
	uint16		path_len;
	char	   *path;
	uint64		data_len;
	uint8		checksum[32];		/* SHA-256 */
} BakDataEntry;

/* Lightweight metadata for one DATA-section entry. */
typedef struct BakDataEntryMeta
{
	char	   *path;
	uint64		data_len;
	uint8		digest[32];
} BakDataEntryMeta;

/* Parsed .bak file header */
typedef struct BakFileHeader
{
	uint16		format_version;
	PgDbBackupMode mode;
	PgDbBackupType type;
	char		db_name[NAMEDATALEN];
	Oid			db_oid;
	uint64		start_lsn;
	uint64		stop_lsn;
	uint32		start_tli;
	uint32		stop_tli;
	uint32		pg_version;
	int64		created_at;			/* microseconds since epoch */
	uint64		base_backup_lsn;	/* for diff/log: references base full */
	bool		compressed;
	char		compression_algo[16];
	bool		encrypted;
	char		encryption_algo[16];
	uint8		key_salt[16];
	uint8		key_iv[12];
	uint32		file_count;
	uint32		frozen_xid;
	uint32		min_mxid;
	uint64		checkpoint_lsn;
} BakFileHeader;

typedef struct BakFileWriter
{
	FILE	   *fp;
	char	   *filepath;
	BakFileHeader header;
	bool		compress;
	char	   *password;
	off_t		section_length_offset;
	uint64		section_bytes;
	bool		section_open;
	pg_cryptohash_ctx *entry_hash_ctx;
	bool		entry_open;
	struct StringInfoData *section_buf;
	bool		section_direct;
} BakFileWriter;

/* Reader state for reading a .bak file */
typedef struct BakFileReader
{
	FILE	   *fp;
	char	   *filepath;
	BakFileHeader header;
	char	   *password;
	uint64		current_section_length;
	uint8		current_section_type;
	char	   *section_plain;
	size_t		section_plain_len;
	size_t		section_plain_pos;
	bool		section_direct;
	off_t		section_end_offset;
} BakFileReader;

/* Writer API */
extern BakFileWriter *bakfile_create(const char *filepath,
									  BakFileHeader *header,
									  bool compress,
									  const char *password);
extern void bakfile_begin_section(BakFileWriter *writer, uint8 section_type);
extern void bakfile_write_section_data(BakFileWriter *writer,
										const void *data, size_t len);
extern void bakfile_end_section(BakFileWriter *writer);
extern void bakfile_begin_data_entry(BakFileWriter *writer,
									  const char *path, uint64 data_len);
extern void bakfile_write_data_entry_chunk(BakFileWriter *writer,
											const void *data, size_t len);
extern void bakfile_end_data_entry(BakFileWriter *writer);
extern void bakfile_close(BakFileWriter *writer);
extern void bakfile_rewrite_header(BakFileWriter *writer,
									BakFileHeader *header);

/* Reader API */
extern BakFileReader *bakfile_open(const char *filepath,
									const char *password);
extern uint8 bakfile_next_section(BakFileReader *reader);
extern size_t bakfile_read_section_data(BakFileReader *reader,
										 void *buf, size_t len);
extern bool bakfile_next_data_entry(BakFileReader *reader,
									 BakDataEntry *entry);
extern size_t bakfile_read_data_entry_chunk(BakFileReader *reader,
											 void *buf, size_t len);
extern void bakfile_finish_data_entry(BakFileReader *reader,
									   BakDataEntry *entry,
									   const void *data, bool verify);
extern uint32 bakfile_read_data_entry_count(BakFileReader *reader);
extern uint64 bakfile_section_remaining(BakFileReader *reader);
extern BakDataEntryMeta *bakfile_list_data_entries(BakFileReader *reader,
													uint32 *count_out);
extern void bakfile_read_section_all(BakFileReader *reader,
									  char **buf_out, size_t *len_out);
extern bool bakfile_verify(const char *filepath, const char *password,
							char **detail);
extern void bakfile_close_reader(BakFileReader *reader);

#endif /* BAKFILE_H */
