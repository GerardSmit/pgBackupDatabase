#ifndef BAKFILE_CRYPTO_H
#define BAKFILE_CRYPTO_H

#include "postgres.h"

#define BAKCRYPTO_SALT_LEN	16
#define BAKCRYPTO_IV_LEN	12
#define BAKCRYPTO_KEY_LEN	32
#define BAKCRYPTO_TAG_LEN	16
#define BAKCRYPTO_PBKDF2_ITERS	100000

typedef struct BakCryptoContext
{
	bool		compress;
	bool		encrypt;
	uint8		key[BAKCRYPTO_KEY_LEN];
	uint8		iv[BAKCRYPTO_IV_LEN];
	uint8		salt[BAKCRYPTO_SALT_LEN];
} BakCryptoContext;

/*
 * Writer-side init. Generates a fresh salt + IV (writing them into the
 * supplied buffers, which must be BAKCRYPTO_SALT_LEN / BAKCRYPTO_IV_LEN bytes)
 * when password != NULL. salt_out / iv_out may be NULL when password is NULL.
 */
extern BakCryptoContext *bakcrypto_writer_init(bool compress,
											   const char *password,
											   uint8 *salt_out,
											   uint8 *iv_out);

/*
 * Reader-side init. salt + iv come from the .bak header. They are ignored
 * when encrypted == false. password is required iff encrypted == true.
 */
extern BakCryptoContext *bakcrypto_reader_init(bool compressed,
											   bool encrypted,
											   const char *password,
											   const uint8 *salt,
											   const uint8 *iv);

/*
 * Writer-side transform: optional zstd compress, then optional AES-256-GCM
 * encrypt. Output is allocated via palloc and returned in *output_out.
 * The returned blob begins with an 8-byte big-endian original-length prefix
 * so bakcrypto_unprocess can recover the plaintext length without an
 * out-of-band hint.
 */
extern size_t bakcrypto_process(BakCryptoContext *ctx,
								const void *input, size_t input_len,
								void **output_out);

/*
 * Reader-side inverse of bakcrypto_process. Consumes the original-length
 * prefix transparently. Output is palloc'd into *output_out.
 */
extern size_t bakcrypto_unprocess(BakCryptoContext *ctx,
								  const void *input, size_t input_len,
								  void **output_out);

extern void bakcrypto_free(BakCryptoContext *ctx);

#endif							/* BAKFILE_CRYPTO_H */
