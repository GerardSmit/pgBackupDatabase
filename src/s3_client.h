#ifndef PG_DBBACKUP_S3_CLIENT_H
#define PG_DBBACKUP_S3_CLIENT_H

#include "postgres.h"
#include "lib/stringinfo.h"

typedef struct PgdbS3Config
{
	char	   *bucket;
	char	   *prefix;
	char	   *region;
	char	   *endpoint_url;
	bool		force_path_style;
	char	   *encryption;
	char	   *kms_key_id;
	char	   *object_lock_mode;
	char	   *object_lock_retain_until;
	int			max_retries;
	int			connect_timeout_ms;
	int			request_timeout_ms;
	long		bandwidth_limit_bps;
} PgdbS3Config;

typedef struct PgdbS3HeadResult
{
	bool		exists;
	uint64		size_bytes;
} PgdbS3HeadResult;

#define PGDB_S3_MULTIPART_THRESHOLD_BYTES ((uint64) 8 * 1024 * 1024)
#define PGDB_S3_MULTIPART_PART_SIZE_BYTES ((uint64) 5 * 1024 * 1024)

typedef struct PgdbS3MultipartPart
{
	int			part_number;
	char	   *etag;
} PgdbS3MultipartPart;

extern void pgdb_s3_create_bucket(PgdbS3Config *config);
extern void pgdb_s3_put_file(PgdbS3Config *config, const char *key,
							 const char *filepath);
extern void pgdb_s3_put_text(PgdbS3Config *config, const char *key,
							 const char *body);
extern void pgdb_s3_get_file(PgdbS3Config *config, const char *key,
							 const char *filepath);
extern char *pgdb_s3_get_text(PgdbS3Config *config, const char *key);
extern void pgdb_s3_delete_object(PgdbS3Config *config, const char *key);
extern PgdbS3HeadResult pgdb_s3_head_object(PgdbS3Config *config,
											const char *key);
extern void pgdb_s3_list_prefix(PgdbS3Config *config, const char *prefix,
								StringInfo out);
extern char *pgdb_s3_create_multipart_upload(PgdbS3Config *config,
											 const char *key);
extern char *pgdb_s3_upload_part(PgdbS3Config *config, const char *key,
								 const char *upload_id, int part_number,
								 const char *filepath, uint64 offset,
								 uint64 length);
extern void pgdb_s3_complete_multipart_upload(PgdbS3Config *config,
											  const char *key,
											  const char *upload_id,
											  PgdbS3MultipartPart *parts,
											  int part_count);
extern void pgdb_s3_abort_multipart_upload(PgdbS3Config *config,
										   const char *key,
										   const char *upload_id);

#endif
