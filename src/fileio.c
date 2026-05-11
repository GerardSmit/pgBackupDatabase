#include "postgres.h"

#include <sys/stat.h>
#include <unistd.h>

#include "storage/fd.h"
#include "utils/builtins.h"

#include "fileio.h"

void
fileio_write_all(FILE *fp, const void *data, size_t len, const char *filepath)
{
	if (fwrite(data, 1, len, fp) != len)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not write to file \"%s\": %m", filepath)));
}

void
fileio_read_all(FILE *fp, void *buf, size_t len, const char *filepath)
{
	if (fread(buf, 1, len, fp) != len)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not read from file \"%s\": %m", filepath)));
}

off_t
fileio_size(const char *filepath)
{
	struct stat st;

	if (stat(filepath, &st) != 0)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not stat file \"%s\": %m", filepath)));
	return st.st_size;
}

bool
fileio_exists(const char *filepath)
{
	struct stat st;

	return stat(filepath, &st) == 0;
}

void
fileio_copy(const char *src, const char *dst)
{
	ereport(ERROR,
			(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
			 errmsg("fileio_copy not yet implemented")));
}

void
fileio_mkdir_p(const char *path)
{
	ereport(ERROR,
			(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
			 errmsg("fileio_mkdir_p not yet implemented")));
}

void
fileio_visit_files(const char *root, const char *rel_prefix,
				   FileVisitor visit, void *ctx)
{
	DIR		   *dir;
	struct dirent *de;

	dir = AllocateDir(root);
	if (dir == NULL)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not open directory \"%s\": %m", root)));

	while ((de = ReadDir(dir, root)) != NULL)
	{
		char	   *abspath;
		char	   *relpath;
		struct stat st;

		if (strcmp(de->d_name, ".") == 0 || strcmp(de->d_name, "..") == 0)
			continue;

		abspath = psprintf("%s/%s", root, de->d_name);

		if (rel_prefix != NULL && rel_prefix[0] != '\0')
			relpath = psprintf("%s/%s", rel_prefix, de->d_name);
		else
			relpath = pstrdup(de->d_name);

		if (lstat(abspath, &st) != 0)
		{
			pfree(abspath);
			pfree(relpath);
			continue;
		}

		if (S_ISDIR(st.st_mode))
		{
			fileio_visit_files(abspath, relpath, visit, ctx);
		}
		else if (S_ISREG(st.st_mode))
		{
			visit(relpath, abspath, ctx);
		}

		pfree(abspath);
		pfree(relpath);
	}

	FreeDir(dir);
}
