#ifndef FILEIO_H
#define FILEIO_H

#include "postgres.h"

extern void fileio_write_all(FILE *fp, const void *data, size_t len,
							  const char *filepath);
extern void fileio_read_all(FILE *fp, void *buf, size_t len,
							 const char *filepath);
extern off_t fileio_size(const char *filepath);
extern bool fileio_exists(const char *filepath);
extern void fileio_copy(const char *src, const char *dst);
extern void fileio_mkdir_p(const char *path);

typedef void (*FileVisitor) (const char *relpath, const char *abspath,
							 void *ctx);

extern void fileio_visit_files(const char *root, const char *rel_prefix,
								FileVisitor visit, void *ctx);

#endif
