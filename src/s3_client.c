#include "postgres.h"

#include <ctype.h>
#include <curl/curl.h>
#include <openssl/hmac.h>
#include <openssl/sha.h>
#include <sys/stat.h>
#include <unistd.h>

#include "lib/stringinfo.h"
#include "miscadmin.h"
#include "port/pg_bswap.h"
#include "storage/fd.h"
#include "utils/builtins.h"
#include "utils/timestamp.h"

#include "s3_client.h"

#define S3_EMPTY_SHA256 \
	"e3b0c44298fc1c149afbf4c8996fb924" \
	"27ae41e4649b934ca495991b7852b855"

typedef struct S3UrlParts
{
	char	   *scheme;
	char	   *host;
	char	   *base_url;
	char	   *canonical_uri;
} S3UrlParts;

typedef struct S3WriteFile
{
	FILE	   *fp;
	const char *path;
} S3WriteFile;

typedef struct S3ReadFile
{
	FILE	   *fp;
	const char *path;
	uint64		remaining;
} S3ReadFile;

typedef struct S3Request S3Request;

static bool		curl_initialized = false;

static char *xml_extract_tag(const char *xml, const char *tag);
static char *xml_unescape(const char *input);

static void
pgdb_s3_init_curl(void)
{
	if (!curl_initialized)
	{
		CURLcode	rc = curl_global_init(CURL_GLOBAL_DEFAULT);

		if (rc != CURLE_OK)
			ereport(ERROR,
					(errcode(ERRCODE_INTERNAL_ERROR),
					 errmsg("could not initialize libcurl: %s",
							curl_easy_strerror(rc))));
		curl_initialized = true;
	}
}

static const char *
required_env(const char *name)
{
	const char *value = getenv(name);

	if (value == NULL || value[0] == '\0')
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("required S3 credential environment variable %s is not set",
						name)));
	return value;
}

static const char *
optional_env(const char *name)
{
	const char *value = getenv(name);

	return (value && value[0] != '\0') ? value : NULL;
}

static char *
trim_trailing_slashes(const char *s)
{
	size_t		len;
	char	   *out;

	if (s == NULL || s[0] == '\0')
		return NULL;

	len = strlen(s);
	while (len > 0 && s[len - 1] == '/')
		len--;

	out = palloc(len + 1);
	memcpy(out, s, len);
	out[len] = '\0';
	return out;
}

static char *
default_endpoint(const char *region)
{
	return psprintf("https://s3.%s.amazonaws.com",
					region && region[0] ? region : "us-east-1");
}

static char *
s3_uri_encode(const char *input, bool encode_slash)
{
	StringInfoData out;
	const unsigned char *p = (const unsigned char *) input;
	static const char hex[] = "0123456789ABCDEF";

	initStringInfo(&out);
	while (*p)
	{
		unsigned char c = *p++;

		if ((c >= 'A' && c <= 'Z') ||
			(c >= 'a' && c <= 'z') ||
			(c >= '0' && c <= '9') ||
			c == '-' || c == '_' || c == '.' || c == '~' ||
			(c == '/' && !encode_slash))
		{
			appendStringInfoChar(&out, (char) c);
		}
		else
		{
			appendStringInfoChar(&out, '%');
			appendStringInfoChar(&out, hex[c >> 4]);
			appendStringInfoChar(&out, hex[c & 0x0f]);
		}
	}
	return out.data;
}

static char *
join_prefix_key(const char *prefix, const char *key)
{
	if (prefix == NULL || prefix[0] == '\0')
		return pstrdup(key);
	if (key == NULL || key[0] == '\0')
		return pstrdup(prefix);
	if (prefix[strlen(prefix) - 1] == '/')
		return psprintf("%s%s", prefix, key);
	return psprintf("%s/%s", prefix, key);
}

static void
parse_endpoint(const char *endpoint, char **scheme_out, char **host_out)
{
	const char *p;
	const char *host_start;
	char	   *trimmed = trim_trailing_slashes(endpoint);

	p = strstr(trimmed, "://");
	if (p)
	{
		*scheme_out = pnstrdup(trimmed, p - trimmed);
		host_start = p + 3;
	}
	else
	{
		*scheme_out = pstrdup("https");
		host_start = trimmed;
	}

	*host_out = pstrdup(host_start);
	pfree(trimmed);
}

static S3UrlParts
build_url_parts(PgdbS3Config *config, const char *key,
				const char *query_string, bool bucket_only)
{
	S3UrlParts	parts;
	char	   *endpoint = config->endpoint_url && config->endpoint_url[0]
		? trim_trailing_slashes(config->endpoint_url)
		: default_endpoint(config->region);
	char	   *scheme;
	char	   *host;
	char	   *safe_key;
	char	   *bucket_and_key;

	parse_endpoint(endpoint, &scheme, &host);
	safe_key = key && key[0] ? s3_uri_encode(key, false) : pstrdup("");

	if (config->force_path_style || bucket_only)
	{
		if (safe_key[0])
			bucket_and_key = psprintf("/%s/%s", config->bucket, safe_key);
		else
			bucket_and_key = psprintf("/%s", config->bucket);

		parts.host = pstrdup(host);
		parts.base_url = psprintf("%s://%s%s%s%s",
								  scheme, host, bucket_and_key,
								  query_string && query_string[0] ? "?" : "",
								  query_string && query_string[0] ? query_string : "");
		parts.canonical_uri = bucket_and_key;
	}
	else
	{
		char	   *vh = psprintf("%s.%s", config->bucket, host);

		if (safe_key[0])
			bucket_and_key = psprintf("/%s", safe_key);
		else
			bucket_and_key = pstrdup("/");

		parts.host = vh;
		parts.base_url = psprintf("%s://%s%s%s%s",
								  scheme, vh, bucket_and_key,
								  query_string && query_string[0] ? "?" : "",
								  query_string && query_string[0] ? query_string : "");
		parts.canonical_uri = bucket_and_key;
	}

	parts.scheme = pstrdup(scheme);
	pfree(endpoint);
	pfree(scheme);
	pfree(host);
	pfree(safe_key);
	return parts;
}

static void
hex_lower(const unsigned char *bytes, int len, char *out)
{
	static const char hex[] = "0123456789abcdef";
	int			i;

	for (i = 0; i < len; i++)
	{
		out[i * 2] = hex[bytes[i] >> 4];
		out[i * 2 + 1] = hex[bytes[i] & 0x0f];
	}
	out[len * 2] = '\0';
}

static void
sha256_bytes(const void *data, size_t len, char hex_out[65])
{
	unsigned char digest[SHA256_DIGEST_LENGTH];

	SHA256((const unsigned char *) data, len, digest);
	hex_lower(digest, SHA256_DIGEST_LENGTH, hex_out);
}

static void
sha256_file_hex(const char *path, char hex_out[65], uint64 *size_out)
{
	FILE	   *fp;
	char	   *buf;
	size_t		n;
	uint64		total = 0;
	SHA256_CTX	ctx;

	fp = AllocateFile(path, "rb");
	if (fp == NULL)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not open \"%s\" for hashing: %m", path)));

	SHA256_Init(&ctx);
	buf = palloc(1024 * 1024);
	while ((n = fread(buf, 1, 1024 * 1024, fp)) > 0)
	{
		CHECK_FOR_INTERRUPTS();
		SHA256_Update(&ctx, buf, n);
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

	{
		unsigned char digest[SHA256_DIGEST_LENGTH];

		SHA256_Final(digest, &ctx);
		hex_lower(digest, SHA256_DIGEST_LENGTH, hex_out);
	}

	if (size_out)
		*size_out = total;
}

static void
sha256_file_range_hex(const char *path, uint64 offset, uint64 length,
					  char hex_out[65])
{
	FILE	   *fp;
	char	   *buf;
	uint64		remaining = length;
	SHA256_CTX	ctx;

	fp = AllocateFile(path, "rb");
	if (fp == NULL)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not open \"%s\" for hashing: %m", path)));

	if (fseeko(fp, (off_t) offset, SEEK_SET) != 0)
	{
		FreeFile(fp);
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not seek \"%s\" while hashing: %m", path)));
	}

	SHA256_Init(&ctx);
	buf = palloc(1024 * 1024);
	while (remaining > 0)
	{
		size_t		want = remaining > 1024 * 1024
			? 1024 * 1024 : (size_t) remaining;
		size_t		n;

		CHECK_FOR_INTERRUPTS();
		n = fread(buf, 1, want, fp);
		if (n == 0)
		{
			if (ferror(fp))
			{
				pfree(buf);
				FreeFile(fp);
				ereport(ERROR,
						(errcode_for_file_access(),
						 errmsg("could not read \"%s\" while hashing: %m",
								path)));
			}
			break;
		}
		SHA256_Update(&ctx, buf, n);
		remaining -= n;
	}

	pfree(buf);
	FreeFile(fp);

	if (remaining != 0)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("short read while hashing S3 multipart range")));

	{
		unsigned char digest[SHA256_DIGEST_LENGTH];

		SHA256_Final(digest, &ctx);
		hex_lower(digest, SHA256_DIGEST_LENGTH, hex_out);
	}
}

static void
hmac_sha256(const unsigned char *key, int key_len,
			const char *data, int data_len,
			unsigned char out[SHA256_DIGEST_LENGTH])
{
	unsigned int len = SHA256_DIGEST_LENGTH;

	HMAC(EVP_sha256(), key, key_len,
		 (const unsigned char *) data, data_len, out, &len);
}

static char *
sign_v4(PgdbS3Config *config, const char *method,
		const char *canonical_uri, const char *canonical_query,
		const char *host, const char *payload_hash,
		const char *amz_date, const char *date_stamp,
		const char *signed_headers, const char *canonical_extra_headers)
{
	const char *access_key = required_env("AWS_ACCESS_KEY_ID");
	const char *secret_key = required_env("AWS_SECRET_ACCESS_KEY");
	StringInfoData canonical;
	StringInfoData string_to_sign;
	char	   *canonical_hash;
	unsigned char k_date[SHA256_DIGEST_LENGTH];
	unsigned char k_region[SHA256_DIGEST_LENGTH];
	unsigned char k_service[SHA256_DIGEST_LENGTH];
	unsigned char k_signing[SHA256_DIGEST_LENGTH];
	unsigned char sig[SHA256_DIGEST_LENGTH];
	char		sig_hex[65];
	char	   *secret;
	char		hash_hex[65];

	initStringInfo(&canonical);
	appendStringInfo(&canonical, "%s\n%s\n%s\n",
					 method, canonical_uri,
					 canonical_query ? canonical_query : "");
	appendStringInfo(&canonical, "host:%s\n", host);
	if (canonical_extra_headers && canonical_extra_headers[0])
		appendStringInfoString(&canonical, canonical_extra_headers);
	appendStringInfo(&canonical,
					 "x-amz-content-sha256:%s\n"
					 "x-amz-date:%s\n"
					 "\n"
					 "%s\n"
					 "%s",
					 payload_hash, amz_date, signed_headers,
					 payload_hash);

	sha256_bytes(canonical.data, canonical.len, hash_hex);
	canonical_hash = pstrdup(hash_hex);

	initStringInfo(&string_to_sign);
	appendStringInfo(&string_to_sign,
					 "AWS4-HMAC-SHA256\n"
					 "%s\n"
					 "%s/%s/s3/aws4_request\n"
					 "%s",
					 amz_date, date_stamp,
					 config->region && config->region[0] ? config->region : "us-east-1",
					 canonical_hash);

	secret = psprintf("AWS4%s", secret_key);
	hmac_sha256((unsigned char *) secret, strlen(secret),
				date_stamp, strlen(date_stamp), k_date);
	hmac_sha256(k_date, SHA256_DIGEST_LENGTH,
				config->region && config->region[0] ? config->region : "us-east-1",
				strlen(config->region && config->region[0] ? config->region : "us-east-1"),
				k_region);
	hmac_sha256(k_region, SHA256_DIGEST_LENGTH, "s3", 2, k_service);
	hmac_sha256(k_service, SHA256_DIGEST_LENGTH,
				"aws4_request", strlen("aws4_request"), k_signing);
	hmac_sha256(k_signing, SHA256_DIGEST_LENGTH,
				string_to_sign.data, string_to_sign.len, sig);
	hex_lower(sig, SHA256_DIGEST_LENGTH, sig_hex);

	return psprintf("AWS4-HMAC-SHA256 Credential=%s/%s/%s/s3/aws4_request, SignedHeaders=%s, Signature=%s",
					access_key, date_stamp,
					config->region && config->region[0] ? config->region : "us-east-1",
					signed_headers, sig_hex);
}

static void
utc_amz_dates(char amz_date[17], char date_stamp[9])
{
	time_t		now = time(NULL);
	struct tm	tm;

#ifdef _WIN32
	gmtime_s(&tm, &now);
#else
	gmtime_r(&now, &tm);
#endif
	strftime(amz_date, 17, "%Y%m%dT%H%M%SZ", &tm);
	strftime(date_stamp, 9, "%Y%m%d", &tm);
}

static void
append_optional_signed_headers(PgdbS3Config *config,
							   StringInfo canonical_extra,
							   StringInfo curl_headers,
							   StringInfo signed_headers)
{
	const char *token = optional_env("AWS_SESSION_TOKEN");

	if (config->encryption && strcmp(config->encryption, "sse-s3") == 0)
	{
		appendStringInfoString(canonical_extra,
							   "x-amz-server-side-encryption:AES256\n");
		appendStringInfoString(curl_headers,
							   "x-amz-server-side-encryption: AES256\n");
		appendStringInfoString(signed_headers,
							   ";x-amz-server-side-encryption");
	}
	else if (config->encryption && strcmp(config->encryption, "sse-kms") == 0)
	{
		appendStringInfoString(canonical_extra,
							   "x-amz-server-side-encryption:aws:kms\n");
		appendStringInfoString(curl_headers,
							   "x-amz-server-side-encryption: aws:kms\n");
		appendStringInfoString(signed_headers,
							   ";x-amz-server-side-encryption");
		if (config->kms_key_id && config->kms_key_id[0])
		{
			appendStringInfo(canonical_extra,
							 "x-amz-server-side-encryption-aws-kms-key-id:%s\n",
							 config->kms_key_id);
			appendStringInfo(curl_headers,
							 "x-amz-server-side-encryption-aws-kms-key-id: %s\n",
							 config->kms_key_id);
			appendStringInfoString(signed_headers,
								   ";x-amz-server-side-encryption-aws-kms-key-id");
		}
	}

	if (config->object_lock_mode &&
		strcmp(config->object_lock_mode, "off") != 0)
	{
		const char *mode = strcmp(config->object_lock_mode, "compliance") == 0
			? "COMPLIANCE" : "GOVERNANCE";

		appendStringInfo(canonical_extra, "x-amz-object-lock-mode:%s\n", mode);
		appendStringInfo(curl_headers, "x-amz-object-lock-mode: %s\n", mode);
		appendStringInfoString(signed_headers, ";x-amz-object-lock-mode");
		if (config->object_lock_retain_until &&
			config->object_lock_retain_until[0])
		{
			appendStringInfo(canonical_extra,
							 "x-amz-object-lock-retain-until-date:%s\n",
							 config->object_lock_retain_until);
			appendStringInfo(curl_headers,
							 "x-amz-object-lock-retain-until-date: %s\n",
							 config->object_lock_retain_until);
			appendStringInfoString(signed_headers,
								   ";x-amz-object-lock-retain-until-date");
		}
	}

	if (token)
	{
		appendStringInfo(canonical_extra, "x-amz-security-token:%s\n", token);
		appendStringInfo(curl_headers, "x-amz-security-token: %s\n", token);
		appendStringInfoString(signed_headers, ";x-amz-security-token");
	}
}

static size_t
read_file_cb(char *ptr, size_t size, size_t nmemb, void *userdata)
{
	S3ReadFile *rf = (S3ReadFile *) userdata;
	size_t		max = size * nmemb;
	size_t		n;

	if (rf->remaining == 0)
		return 0;
	if ((uint64) max > rf->remaining)
		max = (size_t) rf->remaining;

	n = fread(ptr, 1, max, rf->fp);
	if (n > 0)
		rf->remaining -= n;
	return n;
}

static size_t
write_file_cb(char *ptr, size_t size, size_t nmemb, void *userdata)
{
	S3WriteFile *wf = (S3WriteFile *) userdata;
	size_t		n = size * nmemb;

	if (fwrite(ptr, 1, n, wf->fp) != n)
		return 0;
	return n;
}

static size_t
write_string_cb(char *ptr, size_t size, size_t nmemb, void *userdata)
{
	StringInfo	out = (StringInfo) userdata;

	appendBinaryStringInfo(out, ptr, size * nmemb);
	return size * nmemb;
}

struct S3Request
{
	const char *method;
	const char *key;
	const char *query_string;
	const char *canonical_query;
	const char *payload_hash;
	const char *upload_file;
	const char *upload_text;
	const char *download_file;
	bool		head_only;
	bool		bucket_only;
	bool		apply_object_headers;
	uint64		upload_offset;
	uint64		upload_size;
	StringInfo	response_body;
	char	   *response_etag;
	long		status_code;
	curl_off_t	download_size;
};

static size_t
header_cb(char *ptr, size_t size, size_t nmemb, void *userdata)
{
	S3Request  *req = (S3Request *) userdata;
	size_t		len = size * nmemb;

	if (len > 6 && pg_strncasecmp(ptr, "etag:", 5) == 0)
	{
		char	   *line = pnstrdup(ptr + 5, len - 5);
		char	   *start = line;
		char	   *end;

		while (*start && isspace((unsigned char) *start))
			start++;
		end = start + strlen(start);
		while (end > start &&
			   (isspace((unsigned char) end[-1]) || end[-1] == '\r' ||
				end[-1] == '\n'))
			*--end = '\0';
		if (*start == '"' && end > start + 1 && end[-1] == '"')
		{
			start++;
			end[-1] = '\0';
		}
		req->response_etag = pstrdup(start);
		pfree(line);
	}
	return len;
}

static bool
status_retryable(long status)
{
	return status == 408 || status == 409 || status == 425 ||
		status == 429 || status >= 500;
}

static bool
curl_retryable(CURLcode rc)
{
	return rc == CURLE_COULDNT_RESOLVE_HOST ||
		rc == CURLE_COULDNT_CONNECT ||
		rc == CURLE_OPERATION_TIMEDOUT ||
		rc == CURLE_RECV_ERROR ||
		rc == CURLE_SEND_ERROR ||
		rc == CURLE_GOT_NOTHING;
}

static void
perform_request(PgdbS3Config *config, S3Request *req)
{
	int			attempt;
	int			max_retries = config->max_retries > 0 ? config->max_retries : 3;
	CURLcode	last_rc = CURLE_OK;
	long		last_status = 0;
	char		last_err[CURL_ERROR_SIZE] = "";

	pgdb_s3_init_curl();

	for (attempt = 0; attempt <= max_retries; attempt++)
	{
		S3UrlParts	parts;
		CURL	   *curl;
		struct curl_slist *headers = NULL;
		StringInfoData signed_headers;
		StringInfoData canonical_extra;
		StringInfoData curl_extra_headers;
		char		amz_date[17];
		char		date_stamp[9];
		char	   *auth;
		char	   *host_header;
		char	   *sha_header;
		char	   *date_header;
		char	   *auth_header;
		FILE	   *upload_fp = NULL;
		FILE	   *download_fp = NULL;
		S3ReadFile	read_state;
		S3WriteFile write_state;

		CHECK_FOR_INTERRUPTS();
		parts = build_url_parts(config, req->key,
								req->query_string, req->bucket_only);
		initStringInfo(&signed_headers);
		appendStringInfoString(&signed_headers,
							   "host;x-amz-content-sha256;x-amz-date");
		initStringInfo(&canonical_extra);
		initStringInfo(&curl_extra_headers);
		if (req->apply_object_headers)
			append_optional_signed_headers(config, &canonical_extra,
										   &curl_extra_headers,
										   &signed_headers);

		utc_amz_dates(amz_date, date_stamp);
		auth = sign_v4(config, req->method, parts.canonical_uri,
					   req->canonical_query ? req->canonical_query : "",
					   parts.host, req->payload_hash, amz_date, date_stamp,
					   signed_headers.data, canonical_extra.data);

		host_header = psprintf("Host: %s", parts.host);
		sha_header = psprintf("x-amz-content-sha256: %s", req->payload_hash);
		date_header = psprintf("x-amz-date: %s", amz_date);
		auth_header = psprintf("Authorization: %s", auth);
		headers = curl_slist_append(headers, host_header);
		headers = curl_slist_append(headers, sha_header);
		headers = curl_slist_append(headers, date_header);
		headers = curl_slist_append(headers, auth_header);

		if (curl_extra_headers.len > 0)
		{
			char	   *saveptr = NULL;
			char	   *line;
			char	   *copy = pstrdup(curl_extra_headers.data);

			for (line = strtok_r(copy, "\n", &saveptr);
				 line != NULL;
				 line = strtok_r(NULL, "\n", &saveptr))
				headers = curl_slist_append(headers, line);
			pfree(copy);
		}

		curl = curl_easy_init();
		if (curl == NULL)
			ereport(ERROR,
					(errcode(ERRCODE_INTERNAL_ERROR),
					 errmsg("could not allocate libcurl easy handle")));

		curl_easy_setopt(curl, CURLOPT_URL, parts.base_url);
		curl_easy_setopt(curl, CURLOPT_HTTPHEADER, headers);
		curl_easy_setopt(curl, CURLOPT_CUSTOMREQUEST, req->method);
		curl_easy_setopt(curl, CURLOPT_ERRORBUFFER, last_err);
		curl_easy_setopt(curl, CURLOPT_NOSIGNAL, 1L);
		curl_easy_setopt(curl, CURLOPT_HEADERFUNCTION, header_cb);
		curl_easy_setopt(curl, CURLOPT_HEADERDATA, req);
		curl_easy_setopt(curl, CURLOPT_CONNECTTIMEOUT_MS,
						 config->connect_timeout_ms > 0
						 ? config->connect_timeout_ms : 10000);
		curl_easy_setopt(curl, CURLOPT_TIMEOUT_MS,
						 config->request_timeout_ms > 0
						 ? config->request_timeout_ms : 300000);
		if (config->bandwidth_limit_bps > 0)
			curl_easy_setopt(curl, CURLOPT_MAX_SEND_SPEED_LARGE,
							 (curl_off_t) config->bandwidth_limit_bps);

		if (req->head_only)
			curl_easy_setopt(curl, CURLOPT_NOBODY, 1L);
		else if (req->upload_file)
		{
			upload_fp = AllocateFile(req->upload_file, "rb");
			if (upload_fp == NULL)
				ereport(ERROR,
						(errcode_for_file_access(),
						 errmsg("could not open \"%s\" for S3 upload: %m",
								req->upload_file)));
			if (req->upload_offset > 0 &&
				fseeko(upload_fp, (off_t) req->upload_offset, SEEK_SET) != 0)
			{
				FreeFile(upload_fp);
				ereport(ERROR,
						(errcode_for_file_access(),
						 errmsg("could not seek \"%s\" for S3 upload: %m",
								req->upload_file)));
			}
			read_state.fp = upload_fp;
			read_state.path = req->upload_file;
			read_state.remaining = req->upload_size;
			curl_easy_setopt(curl, CURLOPT_UPLOAD, 1L);
			curl_easy_setopt(curl, CURLOPT_READFUNCTION, read_file_cb);
			curl_easy_setopt(curl, CURLOPT_READDATA, &read_state);
			curl_easy_setopt(curl, CURLOPT_INFILESIZE_LARGE,
							 (curl_off_t) req->upload_size);
		}
		else if (req->upload_text)
		{
			curl_easy_setopt(curl, CURLOPT_POSTFIELDS, req->upload_text);
			curl_easy_setopt(curl, CURLOPT_POSTFIELDSIZE_LARGE,
							 (curl_off_t) strlen(req->upload_text));
		}
		else if (strcmp(req->method, "PUT") == 0)
		{
			curl_easy_setopt(curl, CURLOPT_POSTFIELDS, "");
			curl_easy_setopt(curl, CURLOPT_POSTFIELDSIZE_LARGE, (curl_off_t) 0);
		}
		else if (strcmp(req->method, "POST") == 0)
		{
			curl_easy_setopt(curl, CURLOPT_POSTFIELDS, "");
			curl_easy_setopt(curl, CURLOPT_POSTFIELDSIZE_LARGE, (curl_off_t) 0);
		}

		if (req->download_file)
		{
			download_fp = AllocateFile(req->download_file, "wb");
			if (download_fp == NULL)
				ereport(ERROR,
						(errcode_for_file_access(),
						 errmsg("could not open \"%s\" for S3 download: %m",
								req->download_file)));
			write_state.fp = download_fp;
			write_state.path = req->download_file;
			curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, write_file_cb);
			curl_easy_setopt(curl, CURLOPT_WRITEDATA, &write_state);
		}
		else if (req->response_body)
		{
			curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, write_string_cb);
			curl_easy_setopt(curl, CURLOPT_WRITEDATA, req->response_body);
		}

		last_err[0] = '\0';
		last_rc = curl_easy_perform(curl);
		curl_easy_getinfo(curl, CURLINFO_RESPONSE_CODE, &last_status);
		curl_easy_getinfo(curl, CURLINFO_CONTENT_LENGTH_DOWNLOAD_T,
						  &req->download_size);

		if (upload_fp)
			FreeFile(upload_fp);
		if (download_fp)
			FreeFile(download_fp);
		curl_slist_free_all(headers);
		curl_easy_cleanup(curl);
		req->status_code = last_status;

		if (last_rc == CURLE_OK && last_status >= 200 && last_status < 300)
			return;

		if (attempt < max_retries &&
			(curl_retryable(last_rc) || status_retryable(last_status)))
		{
			long		delay_ms = 250L << attempt;

			if (delay_ms > 5000)
				delay_ms = 5000;
			pg_usleep(delay_ms * 1000L);
			continue;
		}

		break;
	}

	if (last_rc != CURLE_OK)
		ereport(ERROR,
				(errcode(ERRCODE_CONNECTION_FAILURE),
				 errmsg("S3 request failed: %s",
						last_err[0] ? last_err : curl_easy_strerror(last_rc))));

	ereport(ERROR,
			(errcode(ERRCODE_CONNECTION_FAILURE),
			 errmsg("S3 request returned HTTP status %ld", last_status)));
}

void
pgdb_s3_create_bucket(PgdbS3Config *config)
{
	S3Request	req;

	memset(&req, 0, sizeof(req));
	req.method = "PUT";
	req.key = "";
	req.payload_hash = S3_EMPTY_SHA256;
	req.bucket_only = true;
	perform_request(config, &req);
}

void
pgdb_s3_put_file(PgdbS3Config *config, const char *key, const char *filepath)
{
	S3Request	req;
	char		hash[65];
	uint64		size = 0;

	sha256_file_hex(filepath, hash, &size);
	memset(&req, 0, sizeof(req));
	req.method = "PUT";
	req.key = join_prefix_key(config->prefix, key);
	req.payload_hash = hash;
	req.upload_file = filepath;
	req.upload_size = size;
	req.apply_object_headers = true;
	perform_request(config, &req);
}

void
pgdb_s3_put_text(PgdbS3Config *config, const char *key, const char *body)
{
	S3Request	req;
	char		hash[65];

	sha256_bytes(body, strlen(body), hash);
	memset(&req, 0, sizeof(req));
	req.method = "PUT";
	req.key = join_prefix_key(config->prefix, key);
	req.payload_hash = hash;
	req.upload_text = body;
	req.apply_object_headers = true;
	perform_request(config, &req);
}

void
pgdb_s3_get_file(PgdbS3Config *config, const char *key, const char *filepath)
{
	S3Request	req;

	memset(&req, 0, sizeof(req));
	req.method = "GET";
	req.key = join_prefix_key(config->prefix, key);
	req.payload_hash = S3_EMPTY_SHA256;
	req.download_file = filepath;
	perform_request(config, &req);
}

char *
pgdb_s3_get_text(PgdbS3Config *config, const char *key)
{
	S3Request	req;
	StringInfoData body;

	initStringInfo(&body);
	memset(&req, 0, sizeof(req));
	req.method = "GET";
	req.key = join_prefix_key(config->prefix, key);
	req.payload_hash = S3_EMPTY_SHA256;
	req.response_body = &body;
	perform_request(config, &req);

	return body.data;
}

void
pgdb_s3_delete_object(PgdbS3Config *config, const char *key)
{
	S3Request	req;

	memset(&req, 0, sizeof(req));
	req.method = "DELETE";
	req.key = join_prefix_key(config->prefix, key);
	req.payload_hash = S3_EMPTY_SHA256;
	perform_request(config, &req);
}

PgdbS3HeadResult
pgdb_s3_head_object(PgdbS3Config *config, const char *key)
{
	S3Request	req;
	PgdbS3HeadResult result;

	memset(&req, 0, sizeof(req));
	req.method = "HEAD";
	req.key = join_prefix_key(config->prefix, key);
	req.payload_hash = S3_EMPTY_SHA256;
	req.head_only = true;

	PG_TRY();
	{
		perform_request(config, &req);
		result.exists = true;
		result.size_bytes = req.download_size >= 0 ? (uint64) req.download_size : 0;
	}
	PG_CATCH();
	{
		if (req.status_code == 404)
		{
			FlushErrorState();
			result.exists = false;
			result.size_bytes = 0;
		}
		else
			PG_RE_THROW();
	}
	PG_END_TRY();

	return result;
}

void
pgdb_s3_list_prefix(PgdbS3Config *config, const char *prefix, StringInfo out)
{
	char	   *full_prefix = join_prefix_key(config->prefix, prefix);
	char	   *encoded = s3_uri_encode(full_prefix, true);
	char	   *continuation = NULL;

	do
	{
		S3Request	req;
		StringInfoData body;
		char	   *query;
		char	   *is_truncated;

		if (continuation)
		{
			char	   *encoded_token = s3_uri_encode(continuation, true);

			query = psprintf("continuation-token=%s&list-type=2&prefix=%s",
							 encoded_token, encoded);
		}
		else
			query = psprintf("list-type=2&prefix=%s", encoded);

		initStringInfo(&body);
		memset(&req, 0, sizeof(req));
		req.method = "GET";
		req.key = "";
		req.query_string = query;
		req.canonical_query = query;
		req.payload_hash = S3_EMPTY_SHA256;
		req.response_body = &body;
		perform_request(config, &req);
		appendBinaryStringInfo(out, body.data, body.len);

		continuation = NULL;
		is_truncated = xml_extract_tag(body.data, "IsTruncated");
		if (is_truncated && strcmp(is_truncated, "true") == 0)
		{
			char	   *raw_token = xml_extract_tag(body.data,
												   "NextContinuationToken");

			if (raw_token == NULL || raw_token[0] == '\0')
				ereport(ERROR,
						(errcode(ERRCODE_DATA_CORRUPTED),
						 errmsg("S3 list response was truncated without a continuation token")));
			continuation = xml_unescape(raw_token);
		}
	}
	while (continuation != NULL);
}

static char *
xml_escape(const char *input)
{
	StringInfoData out;
	const unsigned char *p = (const unsigned char *) input;

	initStringInfo(&out);
	while (*p)
	{
		unsigned char c = *p++;

		switch (c)
		{
			case '&':
				appendStringInfoString(&out, "&amp;");
				break;
			case '<':
				appendStringInfoString(&out, "&lt;");
				break;
			case '>':
				appendStringInfoString(&out, "&gt;");
				break;
			case '"':
				appendStringInfoString(&out, "&quot;");
				break;
			case '\'':
				appendStringInfoString(&out, "&apos;");
				break;
			default:
				appendStringInfoChar(&out, (char) c);
				break;
		}
	}
	return out.data;
}

static char *
xml_unescape(const char *input)
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
xml_extract_tag(const char *xml, const char *tag)
{
	char	   *open = psprintf("<%s>", tag);
	char	   *close = psprintf("</%s>", tag);
	char	   *start = strstr(xml, open);
	char	   *end;
	char	   *value;

	if (start == NULL)
	{
		pfree(open);
		pfree(close);
		return NULL;
	}

	start += strlen(open);
	end = strstr(start, close);
	if (end == NULL)
	{
		pfree(open);
		pfree(close);
		return NULL;
	}

	value = pnstrdup(start, end - start);
	pfree(open);
	pfree(close);
	return value;
}

char *
pgdb_s3_create_multipart_upload(PgdbS3Config *config, const char *key)
{
	S3Request	req;
	StringInfoData body;
	char	   *upload_id;

	initStringInfo(&body);
	memset(&req, 0, sizeof(req));
	req.method = "POST";
	req.key = join_prefix_key(config->prefix, key);
	req.query_string = "uploads=";
	req.canonical_query = "uploads=";
	req.payload_hash = S3_EMPTY_SHA256;
	req.response_body = &body;
	req.apply_object_headers = true;
	perform_request(config, &req);

	upload_id = xml_extract_tag(body.data, "UploadId");
	if (upload_id == NULL || upload_id[0] == '\0')
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("S3 multipart upload did not return an UploadId")));

	return upload_id;
}

char *
pgdb_s3_upload_part(PgdbS3Config *config, const char *key,
					const char *upload_id, int part_number,
					const char *filepath, uint64 offset, uint64 length)
{
	S3Request	req;
	char		hash[65];
	char	   *encoded_upload_id;
	char	   *query;

	if (part_number <= 0 || part_number > 10000)
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("S3 multipart part number must be between 1 and 10000")));
	if (length == 0)
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("S3 multipart part length must be greater than zero")));

	sha256_file_range_hex(filepath, offset, length, hash);
	encoded_upload_id = s3_uri_encode(upload_id, true);
	query = psprintf("partNumber=%d&uploadId=%s", part_number,
					 encoded_upload_id);

	memset(&req, 0, sizeof(req));
	req.method = "PUT";
	req.key = join_prefix_key(config->prefix, key);
	req.query_string = query;
	req.canonical_query = query;
	req.payload_hash = hash;
	req.upload_file = filepath;
	req.upload_offset = offset;
	req.upload_size = length;
	perform_request(config, &req);

	if (req.response_etag == NULL || req.response_etag[0] == '\0')
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("S3 multipart upload part did not return an ETag")));

	return req.response_etag;
}

void
pgdb_s3_complete_multipart_upload(PgdbS3Config *config, const char *key,
								  const char *upload_id,
								  PgdbS3MultipartPart *parts,
								  int part_count)
{
	S3Request	req;
	StringInfoData xml;
	char		hash[65];
	char	   *encoded_upload_id;
	char	   *query;
	int			i;

	if (part_count <= 0)
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("S3 multipart completion requires at least one part")));

	initStringInfo(&xml);
	appendStringInfoString(&xml, "<CompleteMultipartUpload>");
	for (i = 0; i < part_count; i++)
	{
		char	   *safe_etag;

		if (parts[i].part_number <= 0 || parts[i].etag == NULL)
			ereport(ERROR,
					(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
					 errmsg("invalid S3 multipart completion part")));
		safe_etag = xml_escape(parts[i].etag);
		appendStringInfo(&xml,
						 "<Part><PartNumber>%d</PartNumber><ETag>\"%s\"</ETag></Part>",
						 parts[i].part_number, safe_etag);
		pfree(safe_etag);
	}
	appendStringInfoString(&xml, "</CompleteMultipartUpload>");

	sha256_bytes(xml.data, xml.len, hash);
	encoded_upload_id = s3_uri_encode(upload_id, true);
	query = psprintf("uploadId=%s", encoded_upload_id);

	memset(&req, 0, sizeof(req));
	req.method = "POST";
	req.key = join_prefix_key(config->prefix, key);
	req.query_string = query;
	req.canonical_query = query;
	req.payload_hash = hash;
	req.upload_text = xml.data;
	perform_request(config, &req);
}

void
pgdb_s3_abort_multipart_upload(PgdbS3Config *config, const char *key,
							   const char *upload_id)
{
	S3Request	req;
	char	   *encoded_upload_id = s3_uri_encode(upload_id, true);
	char	   *query = psprintf("uploadId=%s", encoded_upload_id);

	memset(&req, 0, sizeof(req));
	req.method = "DELETE";
	req.key = join_prefix_key(config->prefix, key);
	req.query_string = query;
	req.canonical_query = query;
	req.payload_hash = S3_EMPTY_SHA256;
	perform_request(config, &req);
}
