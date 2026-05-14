#ifndef PG_DBBACKUP_DEFAULTS_H
#define PG_DBBACKUP_DEFAULTS_H

/*
 * Chunk size used by the streaming COPY / SHA-256 / S3 read paths.
 * 1 MiB is large enough to amortize per-call overhead on warm caches but
 * still fits comfortably inside any Postgres backend's stack-adjacent
 * allocations.
 */
#define PGDBBACKUP_COPY_CHUNK_SIZE ((size_t) (1024 * 1024))

#endif							/* PG_DBBACKUP_DEFAULTS_H */
