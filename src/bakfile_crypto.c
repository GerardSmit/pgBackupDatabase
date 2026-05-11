#include "postgres.h"

#include <zstd.h>
#include <openssl/evp.h>
#include <openssl/rand.h>

#include "common/scram-common.h"
#include "port.h"

#include "bakfile_crypto.h"

#define ORIG_LEN_PREFIX_BYTES	8

static void
write_be64(uint8 *dst, uint64 v)
{
	dst[0] = (uint8) (v >> 56);
	dst[1] = (uint8) (v >> 48);
	dst[2] = (uint8) (v >> 40);
	dst[3] = (uint8) (v >> 32);
	dst[4] = (uint8) (v >> 24);
	dst[5] = (uint8) (v >> 16);
	dst[6] = (uint8) (v >> 8);
	dst[7] = (uint8) (v);
}

static uint64
read_be64(const uint8 *src)
{
	return ((uint64) src[0] << 56) |
		   ((uint64) src[1] << 48) |
		   ((uint64) src[2] << 40) |
		   ((uint64) src[3] << 32) |
		   ((uint64) src[4] << 24) |
		   ((uint64) src[5] << 16) |
		   ((uint64) src[6] << 8) |
		   ((uint64) src[7]);
}

static void
fill_random(uint8 *buf, size_t len)
{
	if (!pg_strong_random(buf, len))
	{
		if (RAND_bytes(buf, (int) len) != 1)
			ereport(ERROR,
					(errcode(ERRCODE_INTERNAL_ERROR),
					 errmsg("could not generate random bytes for crypto context")));
	}
}

static void
derive_key(const char *password, const uint8 *salt, uint8 *key_out)
{
	int			pwlen = (int) strlen(password);

	if (PKCS5_PBKDF2_HMAC(password, pwlen,
						  salt, BAKCRYPTO_SALT_LEN,
						  BAKCRYPTO_PBKDF2_ITERS,
						  EVP_sha256(),
						  BAKCRYPTO_KEY_LEN, key_out) != 1)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("PBKDF2 key derivation failed")));
}

static size_t
zstd_compress_palloc(const void *src, size_t src_len, void **dst_out)
{
	size_t		bound = ZSTD_compressBound(src_len);
	void	   *buf = palloc(bound);
	size_t		written;

	written = ZSTD_compress(buf, bound, src, src_len, 3);
	if (ZSTD_isError(written))
	{
		pfree(buf);
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("zstd compression failed: %s",
						ZSTD_getErrorName(written))));
	}
	*dst_out = buf;
	return written;
}

static size_t
zstd_decompress_palloc(const void *src, size_t src_len,
					   size_t orig_len, void **dst_out)
{
	void	   *buf;
	size_t		written;

	if (orig_len == 0)
	{
		*dst_out = palloc(1);
		return 0;
	}

	buf = palloc(orig_len);
	written = ZSTD_decompress(buf, orig_len, src, src_len);
	if (ZSTD_isError(written))
	{
		pfree(buf);
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("zstd decompression failed: %s",
						ZSTD_getErrorName(written))));
	}
	if (written != orig_len)
	{
		pfree(buf);
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("zstd decompression produced %zu bytes, expected %zu",
						written, orig_len)));
	}
	*dst_out = buf;
	return written;
}

/*
 * AES-256-GCM encrypt. Output layout: ciphertext (len bytes) || tag (16 bytes).
 */
static size_t
aes_gcm_encrypt_palloc(const uint8 *key, const uint8 *iv,
					   const void *plaintext, size_t len,
					   void **ciphertext_out)
{
	EVP_CIPHER_CTX *ctx;
	uint8	   *out;
	int			outlen1 = 0;
	int			outlen2 = 0;
	size_t		total;

	out = palloc(len + BAKCRYPTO_TAG_LEN);

	ctx = EVP_CIPHER_CTX_new();
	if (ctx == NULL)
	{
		pfree(out);
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not allocate EVP_CIPHER_CTX")));
	}

	if (EVP_EncryptInit_ex(ctx, EVP_aes_256_gcm(), NULL, NULL, NULL) != 1 ||
		EVP_CIPHER_CTX_ctrl(ctx, EVP_CTRL_GCM_SET_IVLEN,
							BAKCRYPTO_IV_LEN, NULL) != 1 ||
		EVP_EncryptInit_ex(ctx, NULL, NULL, key, iv) != 1)
	{
		EVP_CIPHER_CTX_free(ctx);
		pfree(out);
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("AES-256-GCM init failed")));
	}

	if (len > 0)
	{
		if (EVP_EncryptUpdate(ctx, out, &outlen1, plaintext, (int) len) != 1)
		{
			EVP_CIPHER_CTX_free(ctx);
			pfree(out);
			ereport(ERROR,
					(errcode(ERRCODE_INTERNAL_ERROR),
					 errmsg("AES-256-GCM encrypt update failed")));
		}
	}

	if (EVP_EncryptFinal_ex(ctx, out + outlen1, &outlen2) != 1)
	{
		EVP_CIPHER_CTX_free(ctx);
		pfree(out);
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("AES-256-GCM encrypt finalize failed")));
	}

	total = (size_t) (outlen1 + outlen2);

	if (EVP_CIPHER_CTX_ctrl(ctx, EVP_CTRL_GCM_GET_TAG,
							BAKCRYPTO_TAG_LEN, out + total) != 1)
	{
		EVP_CIPHER_CTX_free(ctx);
		pfree(out);
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("AES-256-GCM tag retrieval failed")));
	}

	EVP_CIPHER_CTX_free(ctx);
	*ciphertext_out = out;
	return total + BAKCRYPTO_TAG_LEN;
}

static size_t
aes_gcm_decrypt_palloc(const uint8 *key, const uint8 *iv,
					   const void *ciphertext, size_t len,
					   void **plaintext_out)
{
	EVP_CIPHER_CTX *ctx;
	uint8	   *out;
	const uint8 *ct = ciphertext;
	size_t		ct_len;
	uint8		tag[BAKCRYPTO_TAG_LEN];
	int			outlen1 = 0;
	int			outlen2 = 0;

	if (len < BAKCRYPTO_TAG_LEN)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("encrypted blob too short to contain GCM tag")));

	ct_len = len - BAKCRYPTO_TAG_LEN;
	memcpy(tag, ct + ct_len, BAKCRYPTO_TAG_LEN);

	out = palloc(ct_len == 0 ? 1 : ct_len);

	ctx = EVP_CIPHER_CTX_new();
	if (ctx == NULL)
	{
		pfree(out);
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not allocate EVP_CIPHER_CTX")));
	}

	if (EVP_DecryptInit_ex(ctx, EVP_aes_256_gcm(), NULL, NULL, NULL) != 1 ||
		EVP_CIPHER_CTX_ctrl(ctx, EVP_CTRL_GCM_SET_IVLEN,
							BAKCRYPTO_IV_LEN, NULL) != 1 ||
		EVP_DecryptInit_ex(ctx, NULL, NULL, key, iv) != 1)
	{
		EVP_CIPHER_CTX_free(ctx);
		pfree(out);
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("AES-256-GCM decrypt init failed")));
	}

	if (ct_len > 0)
	{
		if (EVP_DecryptUpdate(ctx, out, &outlen1, ct, (int) ct_len) != 1)
		{
			EVP_CIPHER_CTX_free(ctx);
			pfree(out);
			ereport(ERROR,
					(errcode(ERRCODE_INTERNAL_ERROR),
					 errmsg("AES-256-GCM decrypt update failed")));
		}
	}

	if (EVP_CIPHER_CTX_ctrl(ctx, EVP_CTRL_GCM_SET_TAG,
							BAKCRYPTO_TAG_LEN, tag) != 1)
	{
		EVP_CIPHER_CTX_free(ctx);
		pfree(out);
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("AES-256-GCM tag set failed")));
	}

	if (EVP_DecryptFinal_ex(ctx, out + outlen1, &outlen2) != 1)
	{
		EVP_CIPHER_CTX_free(ctx);
		pfree(out);
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("AES-256-GCM authentication failed"),
				 errhint("Wrong password or corrupted ciphertext.")));
	}

	EVP_CIPHER_CTX_free(ctx);
	*plaintext_out = out;
	return (size_t) (outlen1 + outlen2);
}

BakCryptoContext *
bakcrypto_writer_init(bool compress, const char *password,
					  uint8 *salt_out, uint8 *iv_out)
{
	BakCryptoContext *ctx = palloc0(sizeof(BakCryptoContext));

	ctx->compress = compress;
	ctx->encrypt = (password != NULL);

	if (ctx->encrypt)
	{
		fill_random(ctx->salt, BAKCRYPTO_SALT_LEN);
		fill_random(ctx->iv, BAKCRYPTO_IV_LEN);
		derive_key(password, ctx->salt, ctx->key);

		if (salt_out)
			memcpy(salt_out, ctx->salt, BAKCRYPTO_SALT_LEN);
		if (iv_out)
			memcpy(iv_out, ctx->iv, BAKCRYPTO_IV_LEN);
	}

	return ctx;
}

BakCryptoContext *
bakcrypto_reader_init(bool compressed, bool encrypted, const char *password,
					  const uint8 *salt, const uint8 *iv)
{
	BakCryptoContext *ctx = palloc0(sizeof(BakCryptoContext));

	ctx->compress = compressed;
	ctx->encrypt = encrypted;

	if (encrypted)
	{
		if (password == NULL)
			ereport(ERROR,
					(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
					 errmsg("password required to read encrypted .bak file")));
		if (salt == NULL || iv == NULL)
			ereport(ERROR,
					(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
					 errmsg("salt and IV required to read encrypted .bak file")));

		memcpy(ctx->salt, salt, BAKCRYPTO_SALT_LEN);
		memcpy(ctx->iv, iv, BAKCRYPTO_IV_LEN);
		derive_key(password, ctx->salt, ctx->key);
	}

	return ctx;
}

size_t
bakcrypto_process(BakCryptoContext *ctx,
				  const void *input, size_t input_len,
				  void **output_out)
{
	void	   *stage1 = NULL;
	size_t		stage1_len;
	void	   *stage2 = NULL;
	size_t		stage2_len;
	uint8	   *final_buf;
	size_t		final_len;

	if (ctx->compress)
		stage1_len = zstd_compress_palloc(input, input_len, &stage1);
	else
	{
		stage1 = palloc(input_len == 0 ? 1 : input_len);
		if (input_len > 0)
			memcpy(stage1, input, input_len);
		stage1_len = input_len;
	}

	if (ctx->encrypt)
	{
		stage2_len = aes_gcm_encrypt_palloc(ctx->key, ctx->iv,
											stage1, stage1_len, &stage2);
		pfree(stage1);
	}
	else
	{
		stage2 = stage1;
		stage2_len = stage1_len;
	}

	final_len = ORIG_LEN_PREFIX_BYTES + stage2_len;
	final_buf = palloc(final_len);
	write_be64(final_buf, (uint64) input_len);
	if (stage2_len > 0)
		memcpy(final_buf + ORIG_LEN_PREFIX_BYTES, stage2, stage2_len);
	pfree(stage2);

	*output_out = final_buf;
	return final_len;
}

size_t
bakcrypto_unprocess(BakCryptoContext *ctx,
					const void *input, size_t input_len,
					void **output_out)
{
	const uint8 *in = input;
	uint64		orig_len;
	const uint8 *payload;
	size_t		payload_len;
	void	   *stage1 = NULL;
	size_t		stage1_len;
	void	   *stage2 = NULL;
	size_t		stage2_len;

	if (input_len < ORIG_LEN_PREFIX_BYTES)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("processed blob shorter than length prefix")));

	orig_len = read_be64(in);
	payload = in + ORIG_LEN_PREFIX_BYTES;
	payload_len = input_len - ORIG_LEN_PREFIX_BYTES;

	if (ctx->encrypt)
	{
		stage1_len = aes_gcm_decrypt_palloc(ctx->key, ctx->iv,
											payload, payload_len, &stage1);
	}
	else
	{
		stage1 = palloc(payload_len == 0 ? 1 : payload_len);
		if (payload_len > 0)
			memcpy(stage1, payload, payload_len);
		stage1_len = payload_len;
	}

	if (ctx->compress)
	{
		stage2_len = zstd_decompress_palloc(stage1, stage1_len,
											(size_t) orig_len, &stage2);
		pfree(stage1);
	}
	else
	{
		if (stage1_len != orig_len)
		{
			pfree(stage1);
			ereport(ERROR,
					(errcode(ERRCODE_DATA_CORRUPTED),
					 errmsg("uncompressed payload length mismatch: got %zu, expected %llu",
							stage1_len, (unsigned long long) orig_len)));
		}
		stage2 = stage1;
		stage2_len = stage1_len;
	}

	*output_out = stage2;
	return stage2_len;
}

void
bakcrypto_free(BakCryptoContext *ctx)
{
	if (ctx)
	{
		memset(ctx->key, 0, sizeof(ctx->key));
		pfree(ctx);
	}
}
