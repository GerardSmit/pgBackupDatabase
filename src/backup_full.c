#include "postgres.h"

#include <sys/stat.h>
#include <unistd.h>

#include "access/xlog.h"
#include "access/xlog_internal.h"
#include "access/xlogbackup.h"
#include "access/xlogdefs.h"
#include "catalog/pg_database.h"
#include "common/relpath.h"
#include "executor/spi.h"
#include "lib/stringinfo.h"
#include "miscadmin.h"
#include "port/pg_bswap.h"
#include "storage/block.h"
#include "storage/bufpage.h"
#include "storage/fd.h"
#include "storage/lmgr.h"
#include "storage/relfilelocator.h"
#include "utils/builtins.h"
#include "utils/memutils.h"
#include "utils/pg_lsn.h"
#include "utils/timestamp.h"

#include "backup_full.h"
#include "bakfile.h"
#include "fileio.h"
#include "metadata_gen.h"
#include "wal_filter.h"

/*
 * A relation file segment on disk holds RELSEG_SIZE pages of BLCKSZ bytes.
 * For PG's default 8KB block + 1GB segment, RELSEG_SIZE = 131072.
 */

typedef struct FullBackupCtx
{
	BakFileWriter *writer;
	Oid			db_oid;
	uint32		entry_count;
	uint32		total_files;
} FullBackupCtx;

typedef struct CountFilesCtx
{
	uint32		count;
} CountFilesCtx;

static void
count_db_file(const char *relpath, const char *abspath, void *vctx)
{
	CountFilesCtx *ctx = (CountFilesCtx *) vctx;
	const char *base;
	struct stat st;

	base = strrchr(relpath, '/');
	base = (base != NULL) ? base + 1 : relpath;

	if (strcmp(base, "pg_internal.init") == 0)
		return;
	if (strncmp(base, "pgsql_tmp", 9) == 0)
		return;
	{
		size_t		len = strlen(base);

		if (len >= 5 && strcmp(base + len - 5, ".snap") == 0)
			return;
	}

	if (stat(abspath, &st) != 0)
		return;
	if (!S_ISREG(st.st_mode))
		return;

	ctx->count++;
}

static bool
should_skip_relfile(const char *name)
{
	if (strcmp(name, "pg_internal.init") == 0)
		return true;
	if (strncmp(name, "pgsql_tmp", 9) == 0)
		return true;
	{
		size_t		len = strlen(name);

		if (len >= 5 && strcmp(name + len - 5, ".snap") == 0)
			return true;
	}
	return false;
}

static void
write_file_as_entry(BakFileWriter *writer, const char *relpath,
					const char *abspath)
{
	FILE	   *fp;
	struct stat st;
	uint64		size;
	char	   *buf;
	size_t		nread;

	if (stat(abspath, &st) != 0)
		return;

	if (!S_ISREG(st.st_mode))
		return;

	size = (uint64) st.st_size;

	fp = AllocateFile(abspath, "rb");
	if (fp == NULL)
		return;

	bakfile_begin_data_entry(writer, relpath, size);

	if (size > 0)
	{
		uint64		remaining = size;
		size_t		chunk = 64 * 1024;

		buf = palloc(chunk);
		while (remaining > 0)
		{
			size_t		want = remaining < chunk ? (size_t) remaining : chunk;

			CHECK_FOR_INTERRUPTS();
			nread = fread(buf, 1, want, fp);
			if (nread == 0)
				break;
			bakfile_write_data_entry_chunk(writer, buf, nread);
			remaining -= nread;
		}
		pfree(buf);
	}

	bakfile_end_data_entry(writer);
	FreeFile(fp);
}

static void
visit_db_file(const char *relpath, const char *abspath, void *vctx)
{
	FullBackupCtx *ctx = (FullBackupCtx *) vctx;
	const char *base;

	base = strrchr(relpath, '/');
	base = (base != NULL) ? base + 1 : relpath;

	if (should_skip_relfile(base))
		return;

	ctx->entry_count++;
	if (ctx->total_files > 0)
		ereport(NOTICE,
				(errmsg("backing up file %u/%u: %s",
						ctx->entry_count, ctx->total_files, relpath)));
	else
		ereport(NOTICE,
				(errmsg("backing up file %u: %s",
						ctx->entry_count, relpath)));

	write_file_as_entry(ctx->writer, relpath, abspath);
}

static void
capture_db_xid_bounds(Oid db_oid, uint32 *frozen_xid_out, uint32 *min_mxid_out)
{
	int			ret;
	bool		isnull;
	Datum		d;

	*frozen_xid_out = 0;
	*min_mxid_out = 0;

	SPI_connect();
	ret = SPI_execute_with_args(
		"SELECT datfrozenxid::text::int8, datminmxid::text::int8 "
		"FROM pg_database WHERE oid = $1",
		1,
		(Oid[]){OIDOID},
		(Datum[]){ObjectIdGetDatum(db_oid)},
		NULL, true, 1);

	if (ret == SPI_OK_SELECT && SPI_processed == 1)
	{
		d = SPI_getbinval(SPI_tuptable->vals[0],
						   SPI_tuptable->tupdesc, 1, &isnull);
		if (!isnull)
			*frozen_xid_out = (uint32) DatumGetInt64(d);
		d = SPI_getbinval(SPI_tuptable->vals[0],
						   SPI_tuptable->tupdesc, 2, &isnull);
		if (!isnull)
			*min_mxid_out = (uint32) DatumGetInt64(d);
	}
	SPI_finish();
}

static char *
generate_slot_name(void)
{
	uint32		r1 = (uint32) random();
	uint32		r2 = (uint32) random();

	return psprintf("_pg_dbbackup_%08x%08x", r1, r2);
}

static void
create_replication_slot(const char *slot_name)
{
	int			ret;

	SPI_connect();
	ret = SPI_execute_with_args(
		"SELECT pg_create_physical_replication_slot($1, true, true)",
		1,
		(Oid[]){TEXTOID},
		(Datum[]){CStringGetTextDatum(slot_name)},
		NULL, false, 1);
	SPI_finish();

	if (ret != SPI_OK_SELECT)
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not create replication slot \"%s\" (rc=%d)",
						slot_name, ret)));
}

static void
drop_replication_slot_if_exists(const char *slot_name)
{
	int			ret;

	SPI_connect();
	ret = SPI_execute_with_args(
		"SELECT pg_drop_replication_slot($1) "
		"WHERE EXISTS (SELECT 1 FROM pg_replication_slots WHERE slot_name = $1)",
		1,
		(Oid[]){TEXTOID},
		(Datum[]){CStringGetTextDatum(slot_name)},
		NULL, false, 1);
	SPI_finish();

	if (ret != SPI_OK_SELECT)
		elog(WARNING, "could not drop replication slot \"%s\" (rc=%d)",
			 slot_name, ret);
}

typedef struct WalRange
{
	XLogRecPtr	start;
	XLogRecPtr	stop;
	TimeLineID	start_tli;
	TimeLineID	stop_tli;
} WalRange;

void
backup_full_full(Oid db_oid, const char *db_name, const char *filepath,
				 bool compress, const char *password)
{
	BakFileHeader header;
	BakFileWriter *writer;
	StringInfo	metadata_sql;
	BackupState *bstate;
	MemoryContext backup_ctx;
	MemoryContext oldctx;
	FILE	   *probe;
	FullBackupCtx ctx;
	WalRange	wal_range;
	char	   *slot_name;
	bool		backup_started = false;

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

	slot_name = generate_slot_name();

	PG_TRY();
	{
		create_replication_slot(slot_name);

		backup_ctx = AllocSetContextCreate(TopMemoryContext,
										   "pg_dbbackup full backup",
										   ALLOCSET_START_SMALL_SIZES);
		oldctx = MemoryContextSwitchTo(backup_ctx);
		bstate = palloc0_object(BackupState);
		MemoryContextSwitchTo(oldctx);

		register_persistent_abort_backup_handler();
		do_pg_backup_start("pg_dbbackup", true, NULL, bstate, NULL);
		backup_started = true;

		wal_range.start = bstate->startpoint;
		wal_range.start_tli = bstate->starttli;

		metadata_sql = metadata_gen_all(db_oid);

		memset(&header, 0, sizeof(header));
		header.format_version = BAKFILE_VERSION;
		header.mode = BACKUP_MODE_FULL;
		header.type = BACKUP_TYPE_FULL;
		strlcpy(header.db_name, db_name, sizeof(header.db_name));
		header.db_oid = db_oid;
		header.start_lsn = (uint64) bstate->startpoint;
		header.stop_lsn = (uint64) bstate->startpoint;
		header.start_tli = bstate->starttli;
		header.stop_tli = bstate->starttli;
		header.pg_version = PG_VERSION_NUM;
		header.created_at = GetCurrentTimestamp();
		header.base_backup_lsn = 0;
		header.compressed = compress;
		header.encrypted = (password != NULL);
		header.file_count = 0;
		header.checkpoint_lsn = (uint64) bstate->checkpointloc;
		capture_db_xid_bounds(db_oid, &header.frozen_xid, &header.min_mxid);

		writer = bakfile_create(filepath, &header, compress, password);

		bakfile_begin_section(writer, BAKSECTION_METADATA);
		if (metadata_sql && metadata_sql->len > 0)
			bakfile_write_section_data(writer, metadata_sql->data,
									   metadata_sql->len);
		bakfile_end_section(writer);

		ctx.writer = writer;
		ctx.db_oid = db_oid;
		ctx.entry_count = 0;
		ctx.total_files = 0;

		{
			CountFilesCtx count_ctx = {0};
			char		basedir[MAXPGPATH];

			snprintf(basedir, sizeof(basedir), "%s/base/%u",
					 DataDir, db_oid);
			if (fileio_exists(basedir))
			{
				char		relprefix[MAXPGPATH];

				snprintf(relprefix, sizeof(relprefix), "base/%u", db_oid);
				fileio_visit_files(basedir, relprefix, count_db_file,
								   &count_ctx);
			}

			{
				const char *globals[] = {
					"global/pg_filenode.map",
					"global/pg_control",
					NULL
				};
				int			i;

				for (i = 0; globals[i] != NULL; i++)
				{
					char		abspath[MAXPGPATH];

					snprintf(abspath, sizeof(abspath), "%s/%s",
							 DataDir, globals[i]);
					if (fileio_exists(abspath))
						count_ctx.count++;
				}
			}

			{
				char		tblspc_root[MAXPGPATH];
				DIR		   *tbldir;
				struct dirent *de;

				snprintf(tblspc_root, sizeof(tblspc_root), "%s/pg_tblspc",
						 DataDir);

				tbldir = AllocateDir(tblspc_root);
				if (tbldir != NULL)
				{
					while ((de = ReadDir(tbldir, tblspc_root)) != NULL)
					{
						char		tblspc_db[MAXPGPATH];

						if (strcmp(de->d_name, ".") == 0
							|| strcmp(de->d_name, "..") == 0)
							continue;

						snprintf(tblspc_db, sizeof(tblspc_db),
								 "%s/%s/" TABLESPACE_VERSION_DIRECTORY "/%u",
								 tblspc_root, de->d_name, db_oid);

						if (!fileio_exists(tblspc_db))
							continue;

						fileio_visit_files(tblspc_db, "", count_db_file,
										   &count_ctx);
					}
					FreeDir(tbldir);
				}
			}

			ctx.total_files = count_ctx.count;
			ereport(NOTICE,
					(errmsg("FULL FULL backup of \"%s\": %u files to copy",
							db_name, ctx.total_files)));
		}

		bakfile_begin_section(writer, BAKSECTION_DATA);
		{
			uint32		zero_count = 0;

			bakfile_write_section_data(writer, &zero_count, sizeof(zero_count));
		}

		{
			char		basedir[MAXPGPATH];

			snprintf(basedir, sizeof(basedir), "%s/base/%u",
					 DataDir, db_oid);
			if (fileio_exists(basedir))
			{
				char		relprefix[MAXPGPATH];

				snprintf(relprefix, sizeof(relprefix), "base/%u", db_oid);
				fileio_visit_files(basedir, relprefix, visit_db_file, &ctx);
			}
		}

		{
			const char *globals[] = {
				"global/pg_filenode.map",
				"global/pg_control",
				NULL
			};
			int			i;

			for (i = 0; globals[i] != NULL; i++)
			{
				char		abspath[MAXPGPATH];

				snprintf(abspath, sizeof(abspath), "%s/%s",
						 DataDir, globals[i]);
				if (fileio_exists(abspath))
				{
					ctx.entry_count++;
					if (ctx.total_files > 0)
						ereport(NOTICE,
								(errmsg("backing up file %u/%u: %s",
										ctx.entry_count, ctx.total_files,
										globals[i])));
					write_file_as_entry(writer, globals[i], abspath);
				}
			}
		}

		{
			char		tblspc_root[MAXPGPATH];
			DIR		   *tbldir;
			struct dirent *de;

			snprintf(tblspc_root, sizeof(tblspc_root), "%s/pg_tblspc",
					 DataDir);

			tbldir = AllocateDir(tblspc_root);
			if (tbldir != NULL)
			{
				while ((de = ReadDir(tbldir, tblspc_root)) != NULL)
				{
					char		tblspc_db[MAXPGPATH];
					char		relprefix[MAXPGPATH];

					if (strcmp(de->d_name, ".") == 0
						|| strcmp(de->d_name, "..") == 0)
						continue;

					snprintf(tblspc_db, sizeof(tblspc_db),
							 "%s/%s/" TABLESPACE_VERSION_DIRECTORY "/%u",
							 tblspc_root, de->d_name, db_oid);

					if (!fileio_exists(tblspc_db))
						continue;

					snprintf(relprefix, sizeof(relprefix),
							 "pg_tblspc/%s/" TABLESPACE_VERSION_DIRECTORY "/%u",
							 de->d_name, db_oid);

					fileio_visit_files(tblspc_db, relprefix,
									   visit_db_file, &ctx);
				}
				FreeDir(tbldir);
			}
		}

		{
			uint32		net_count = pg_hton32(ctx.entry_count);

			Assert(writer->section_buf != NULL);
			Assert(writer->section_buf->len >= sizeof(net_count));
			memcpy(writer->section_buf->data, &net_count, sizeof(net_count));
		}

		bakfile_end_section(writer);

		do_pg_backup_stop(bstate, true);
		backup_started = false;

		wal_range.stop = bstate->stoppoint;
		wal_range.stop_tli = bstate->stoptli;

		bakfile_begin_section(writer, BAKSECTION_WAL);
		if (wal_range.stop > wal_range.start)
		{
			WalFilterState *filter;

			ereport(NOTICE,
					(errmsg("extracting WAL [%X/%X, %X/%X] (%lu bytes)",
							LSN_FORMAT_ARGS(wal_range.start),
							LSN_FORMAT_ARGS(wal_range.stop),
							(unsigned long) (wal_range.stop - wal_range.start))));

			filter = wal_filter_init(db_oid, wal_range.start,
									  wal_range.stop, wal_range.stop_tli);
			wal_filter_extract(filter, writer);
			wal_filter_free(filter);
		}
		bakfile_end_section(writer);

		bakfile_begin_section(writer, BAKSECTION_WAL_SEGMENTS);
		{
			uint32		zero_count = 0;

			bakfile_write_section_data(writer, &zero_count, sizeof(zero_count));
		}
		if (wal_range.stop > wal_range.start)
		{
			uint64		segs;

			segs = wal_segments_capture(writer, wal_range.start,
										wal_range.stop, wal_range.stop_tli);
			{
				uint32		net_count = pg_hton32((uint32) segs);

				Assert(writer->section_buf != NULL);
				Assert(writer->section_buf->len >= sizeof(net_count));
				memcpy(writer->section_buf->data, &net_count, sizeof(net_count));
			}
			ereport(NOTICE,
					(errmsg("captured %lu WAL segment(s) for PITR",
							(unsigned long) segs)));
		}
		bakfile_end_section(writer);

		header.stop_lsn = (uint64) wal_range.stop;
		header.stop_tli = wal_range.stop_tli;
		bakfile_rewrite_header(writer, &header);

		bakfile_close(writer);

		MemoryContextDelete(backup_ctx);
	}
	PG_CATCH();
	{
		if (backup_started)
		{
			PG_TRY();
			{
				do_pg_backup_stop(bstate, false);
			}
			PG_CATCH();
			{
				FlushErrorState();
			}
			PG_END_TRY();
		}

		PG_TRY();
		{
			drop_replication_slot_if_exists(slot_name);
		}
		PG_CATCH();
		{
			FlushErrorState();
		}
		PG_END_TRY();

		UnlockSharedObject(DatabaseRelationId, db_oid, 0, AccessShareLock);
		PG_RE_THROW();
	}
	PG_END_TRY();

	drop_replication_slot_if_exists(slot_name);

	pfree(slot_name);

	UnlockSharedObject(DatabaseRelationId, db_oid, 0, AccessShareLock);

	ereport(NOTICE,
			(errmsg("FULL FULL backup of \"%s\" complete: %u files written to \"%s\"",
					db_name, ctx.entry_count, filepath)));
}

typedef struct DiffPath
{
	char	   *relpath;
	char	   *abspath;
} DiffPath;

typedef struct DiffCollectCtx
{
	DiffPath  *paths;
	uint32		count;
	uint32		cap;
	int64		base_created_at_us;
} DiffCollectCtx;

static void
diff_paths_append(DiffCollectCtx *ctx, const char *relpath, const char *abspath)
{
	if (ctx->count == ctx->cap)
	{
		uint32		new_cap = ctx->cap == 0 ? 16 : ctx->cap * 2;

		if (ctx->paths == NULL)
			ctx->paths = palloc(sizeof(DiffPath) * new_cap);
		else
			ctx->paths = repalloc(ctx->paths, sizeof(DiffPath) * new_cap);
		ctx->cap = new_cap;
	}
	ctx->paths[ctx->count].relpath = pstrdup(relpath);
	ctx->paths[ctx->count].abspath = pstrdup(abspath);
	ctx->count++;
}

static void
collect_diff_path(const char *relpath, const char *abspath, void *vctx)
{
	DiffCollectCtx *ctx = (DiffCollectCtx *) vctx;
	const char *base;
	struct stat st;
	int64		mtime_us;

	base = strrchr(relpath, '/');
	base = (base != NULL) ? base + 1 : relpath;

	if (should_skip_relfile(base))
		return;

	if (stat(abspath, &st) != 0)
		return;
	if (!S_ISREG(st.st_mode))
		return;

	mtime_us = ((int64) st.st_mtime) * INT64CONST(1000000);

	if (mtime_us <= ctx->base_created_at_us)
		return;

	diff_paths_append(ctx, relpath, abspath);
}

/*
 * A single relation fork that changed since the base FULL, as discovered via
 * WAL summaries. limit_all=true means we lost block-level detail and must
 * include the whole on-disk file (every segment).
 */
typedef struct ChangedRel
{
	Oid			relfilenode;
	int16		forknum;
	BlockNumber *blocks;
	uint32		nblocks;
	uint32		blocks_cap;
	bool		limit_all;
} ChangedRel;

typedef struct ChangedRelSet
{
	ChangedRel *rels;
	uint32		count;
	uint32		cap;
} ChangedRelSet;

static ChangedRel *
changed_rel_set_get_or_add(ChangedRelSet *set, Oid relfilenode, int16 forknum)
{
	uint32		i;
	ChangedRel *r;

	for (i = 0; i < set->count; i++)
	{
		if (set->rels[i].relfilenode == relfilenode &&
			set->rels[i].forknum == forknum)
			return &set->rels[i];
	}

	if (set->count == set->cap)
	{
		uint32		new_cap = set->cap == 0 ? 32 : set->cap * 2;

		if (set->rels == NULL)
			set->rels = palloc(sizeof(ChangedRel) * new_cap);
		else
			set->rels = repalloc(set->rels, sizeof(ChangedRel) * new_cap);
		set->cap = new_cap;
	}

	r = &set->rels[set->count++];
	r->relfilenode = relfilenode;
	r->forknum = forknum;
	r->blocks = NULL;
	r->nblocks = 0;
	r->blocks_cap = 0;
	r->limit_all = false;
	return r;
}

static void
changed_rel_add_block(ChangedRel *r, BlockNumber blk)
{
	if (r->nblocks == r->blocks_cap)
	{
		uint32		new_cap = r->blocks_cap == 0 ? 16 : r->blocks_cap * 2;

		if (r->blocks == NULL)
			r->blocks = palloc(sizeof(BlockNumber) * new_cap);
		else
			r->blocks = repalloc(r->blocks, sizeof(BlockNumber) * new_cap);
		r->blocks_cap = new_cap;
	}
	r->blocks[r->nblocks++] = blk;
}

/*
 * Return true if summarize_wal=on. The summarizer GUC controls whether the
 * background worker is producing summary files; when off there's no point
 * trying to query pg_available_wal_summaries().
 */
static bool
summarize_wal_enabled(void)
{
	int			ret;
	bool		enabled = false;
	bool		isnull;
	Datum		d;

	SPI_connect();
	ret = SPI_execute("SELECT current_setting('summarize_wal', true) = 'on'",
					  true, 1);
	if (ret == SPI_OK_SELECT && SPI_processed == 1)
	{
		d = SPI_getbinval(SPI_tuptable->vals[0],
						   SPI_tuptable->tupdesc, 1, &isnull);
		if (!isnull)
			enabled = DatumGetBool(d);
	}
	SPI_finish();
	return enabled;
}

/*
 * Block until the summarizer has produced summaries covering at least
 * stop_lsn, or give up after a brief grace period. The summarizer is async
 * and may lag a backup that just generated the last WAL we care about.
 */
static void
wait_for_summarizer(XLogRecPtr stop_lsn)
{
	const int	max_attempts = 60;
	int			attempt;

	for (attempt = 0; attempt < max_attempts; attempt++)
	{
		int			ret;
		bool		isnull;
		Datum		d;
		XLogRecPtr	summarized = InvalidXLogRecPtr;

		SPI_connect();
		ret = SPI_execute(
			"SELECT summarized_lsn FROM pg_get_wal_summarizer_state()",
			true, 1);
		if (ret == SPI_OK_SELECT && SPI_processed == 1)
		{
			d = SPI_getbinval(SPI_tuptable->vals[0],
							   SPI_tuptable->tupdesc, 1, &isnull);
			if (!isnull)
				summarized = DatumGetLSN(d);
		}
		SPI_finish();

		if (summarized >= stop_lsn)
			return;

		pg_usleep(100000L);
		CHECK_FOR_INTERRUPTS();
	}
}

/*
 * Query WAL summaries in [start_lsn, stop_lsn] for db_oid and populate `set`.
 */
static void
collect_wal_summary_changes(Oid db_oid, XLogRecPtr start_lsn,
							 XLogRecPtr stop_lsn, ChangedRelSet *set)
{
	int			ret;
	int			i;
	MemoryContext caller_ctx = CurrentMemoryContext;

	/* Find summary files whose end_lsn > start_lsn and start_lsn < stop_lsn. */
	SPI_connect();
	ret = SPI_execute_with_args(
		"SELECT tli, start_lsn, end_lsn "
		"FROM pg_available_wal_summaries() "
		"WHERE end_lsn > $1 AND start_lsn < $2 "
		"ORDER BY tli, start_lsn",
		2,
		(Oid[]){LSNOID, LSNOID},
		(Datum[]){LSNGetDatum(start_lsn), LSNGetDatum(stop_lsn)},
		NULL, true, 0);

	if (ret != SPI_OK_SELECT)
	{
		SPI_finish();
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("pg_available_wal_summaries() failed (rc=%d)", ret)));
	}

	if (SPI_processed == 0)
	{
		SPI_finish();
		return;
	}

	{
		uint64		nsummaries = SPI_processed;
		struct WalSummaryRow {
			int64		tli;
			XLogRecPtr	start;
			XLogRecPtr	end;
		}		   *summaries;

		summaries = MemoryContextAlloc(caller_ctx,
									    sizeof(*summaries) * nsummaries);

		for (i = 0; i < (int) nsummaries; i++)
		{
			bool		isnull;
			Datum		d;

			d = SPI_getbinval(SPI_tuptable->vals[i],
							   SPI_tuptable->tupdesc, 1, &isnull);
			summaries[i].tli = DatumGetInt64(d);
			d = SPI_getbinval(SPI_tuptable->vals[i],
							   SPI_tuptable->tupdesc, 2, &isnull);
			summaries[i].start = DatumGetLSN(d);
			d = SPI_getbinval(SPI_tuptable->vals[i],
							   SPI_tuptable->tupdesc, 3, &isnull);
			summaries[i].end = DatumGetLSN(d);
		}
		SPI_finish();

		for (i = 0; i < (int) nsummaries; i++)
		{
			uint64		j;

			SPI_connect();
			ret = SPI_execute_with_args(
				"SELECT relfilenode, relforknumber, relblocknumber, is_limit_block "
				"FROM pg_wal_summary_contents($1, $2, $3) "
				"WHERE reldatabase = $4",
				4,
				(Oid[]){INT8OID, LSNOID, LSNOID, OIDOID},
				(Datum[]){
					Int64GetDatum(summaries[i].tli),
					LSNGetDatum(summaries[i].start),
					LSNGetDatum(summaries[i].end),
					ObjectIdGetDatum(db_oid),
				},
				NULL, true, 0);

			if (ret != SPI_OK_SELECT)
			{
				SPI_finish();
				ereport(ERROR,
						(errcode(ERRCODE_INTERNAL_ERROR),
						 errmsg("pg_wal_summary_contents() failed (rc=%d)", ret)));
			}

			for (j = 0; j < SPI_processed; j++)
			{
				bool		isnull;
				Datum		d;
				Oid			rfnode;
				int16		fork;
				int64		blockno;
				bool		is_limit;
				ChangedRel *cr;
				MemoryContext spi_ctx;

				d = SPI_getbinval(SPI_tuptable->vals[j],
								   SPI_tuptable->tupdesc, 1, &isnull);
				rfnode = DatumGetObjectId(d);
				d = SPI_getbinval(SPI_tuptable->vals[j],
								   SPI_tuptable->tupdesc, 2, &isnull);
				fork = (int16) DatumGetInt16(d);
				d = SPI_getbinval(SPI_tuptable->vals[j],
								   SPI_tuptable->tupdesc, 3, &isnull);
				blockno = DatumGetInt64(d);
				d = SPI_getbinval(SPI_tuptable->vals[j],
								   SPI_tuptable->tupdesc, 4, &isnull);
				is_limit = DatumGetBool(d);

				spi_ctx = MemoryContextSwitchTo(caller_ctx);
				cr = changed_rel_set_get_or_add(set, rfnode, fork);
				if (is_limit)
					cr->limit_all = true;
				else if (!cr->limit_all)
					changed_rel_add_block(cr, (BlockNumber) blockno);
				MemoryContextSwitchTo(spi_ctx);
			}

			SPI_finish();
		}

		pfree(summaries);
	}
}

/*
 * Build the on-disk file path for a relation fork segment within base/<dboid>/.
 * Segment 0 of the main fork is just "<relnode>"; segment k is "<relnode>.k".
 * Non-main forks: "<relnode>_<forkname>" or "<relnode>_<forkname>.k".
 */
static void
build_relfile_path(char *out, size_t outlen, Oid db_oid, Oid relnode,
					int16 forknum, uint32 segno)
{
	const char *forkname = NULL;

	if (forknum != MAIN_FORKNUM)
	{
		if (forknum < 0 || forknum > MAX_FORKNUM)
			return;
		forkname = forkNames[forknum];
	}

	if (forkname == NULL)
	{
		if (segno == 0)
			snprintf(out, outlen, "base/%u/%u", db_oid, relnode);
		else
			snprintf(out, outlen, "base/%u/%u.%u", db_oid, relnode, segno);
	}
	else
	{
		if (segno == 0)
			snprintf(out, outlen, "base/%u/%u_%s", db_oid, relnode, forkname);
		else
			snprintf(out, outlen, "base/%u/%u_%s.%u",
					 db_oid, relnode, forkname, segno);
	}
}

/*
 * Write a single 8KB block from a relation segment as a DATA entry using the
 * block-level path scheme: "base/<dboid>/<relnode>:<fork>:<blockno>".
 */
static bool
write_block_entry(BakFileWriter *writer, Oid db_oid, Oid relnode,
				   int16 forknum, BlockNumber blockno)
{
	char		segpath[MAXPGPATH];
	char		abspath[MAXPGPATH];
	uint32		segno = blockno / RELSEG_SIZE;
	uint32		segoff = (blockno % RELSEG_SIZE);
	off_t		offset = (off_t) segoff * BLCKSZ;
	const char *forkname = (forknum == MAIN_FORKNUM)
		? "main" : forkNames[forknum];
	char		entry_path[MAXPGPATH];
	FILE	   *fp;
	char		buf[BLCKSZ];
	size_t		nread;

	build_relfile_path(segpath, sizeof(segpath), db_oid, relnode, forknum, segno);
	snprintf(abspath, sizeof(abspath), "%s/%s", DataDir, segpath);

	if (!fileio_exists(abspath))
		return false;

	fp = AllocateFile(abspath, "rb");
	if (fp == NULL)
		return false;

	if (fseeko(fp, offset, SEEK_SET) != 0)
	{
		FreeFile(fp);
		return false;
	}

	nread = fread(buf, 1, BLCKSZ, fp);
	FreeFile(fp);

	if (nread == 0)
		return false;

	/* Pad short reads (last partial page) with zeros. */
	if (nread < BLCKSZ)
		memset(buf + nread, 0, BLCKSZ - nread);

	snprintf(entry_path, sizeof(entry_path), "base/%u/%u:%s:%u",
			 db_oid, relnode, forkname, blockno);

	bakfile_begin_data_entry(writer, entry_path, BLCKSZ);
	bakfile_write_data_entry_chunk(writer, buf, BLCKSZ);
	bakfile_end_data_entry(writer);
	return true;
}

/*
 * Append on-disk segments for (relnode, forknum) to diff path list. Used when
 * a relation appears in summaries with is_limit_block=true (lost detail),
 * or when summaries are unavailable.
 */
static void
append_whole_relfork(DiffCollectCtx *ctx, Oid db_oid, Oid relnode, int16 forknum)
{
	uint32		segno;

	for (segno = 0;; segno++)
	{
		char		relpath[MAXPGPATH];
		char		abspath[MAXPGPATH];

		build_relfile_path(relpath, sizeof(relpath), db_oid, relnode,
							forknum, segno);
		snprintf(abspath, sizeof(abspath), "%s/%s", DataDir, relpath);

		if (!fileio_exists(abspath))
			break;

		diff_paths_append(ctx, relpath, abspath);
	}
}

static void
read_base_header(const char *base_filepath, const char *password,
				  const char *expected_db_name,
				  XLogRecPtr *base_stop_lsn_out,
				  int64 *base_created_at_us_out,
				  PgDbBackupType *base_type_out)
{
	BakFileReader *reader;

	reader = bakfile_open(base_filepath, password);

	if (reader->header.mode != BACKUP_MODE_FULL)
	{
		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("base backup \"%s\" is not FULL mode", base_filepath)));
	}

	if (strcmp(reader->header.db_name, expected_db_name) != 0)
	{
		char	   *got = pstrdup(reader->header.db_name);

		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("base backup is for db \"%s\", not \"%s\"",
						got, expected_db_name)));
	}

	*base_stop_lsn_out = (XLogRecPtr) reader->header.stop_lsn;
	*base_created_at_us_out = reader->header.created_at;
	*base_type_out = reader->header.type;

	bakfile_close_reader(reader);
}

void
backup_full_differential(Oid db_oid, const char *db_name, const char *filepath,
						 const char *base_filepath, bool compress,
						 const char *password)
{
	BakFileHeader header;
	BakFileWriter *writer;
	StringInfo	metadata_sql;
	BackupState *bstate;
	MemoryContext backup_ctx;
	MemoryContext oldctx;
	FILE	   *probe;
	DiffCollectCtx collect;
	ChangedRelSet changed;
	WalRange	wal_range;
	char	   *slot_name;
	bool		backup_started = false;
	XLogRecPtr	base_stop_lsn = InvalidXLogRecPtr;
	int64		base_created_at_us = 0;
	PgDbBackupType base_type = BACKUP_TYPE_FULL;
	bool		use_summaries;
	uint32		j;
	uint32		block_entries = 0;

	if (!superuser())
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
				 errmsg("must be superuser to perform backups")));

	if (base_filepath == NULL || base_filepath[0] == '\0')
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("FULL differential backup requires base_filepath"),
				 errhint("Pass the path to the base FULL .bak via base_filepath := ...")));

	read_base_header(base_filepath, password, db_name, &base_stop_lsn,
					  &base_created_at_us, &base_type);

	if (base_type != BACKUP_TYPE_FULL)
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("base for differential must be type=full, got a different type")));

	probe = AllocateFile(filepath, "wb");
	if (probe == NULL)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not open backup destination \"%s\": %m",
						filepath)));
	FreeFile(probe);
	(void) unlink(filepath);

	LockSharedObject(DatabaseRelationId, db_oid, 0, AccessShareLock);

	slot_name = generate_slot_name();

	PG_TRY();
	{
		create_replication_slot(slot_name);

		backup_ctx = AllocSetContextCreate(TopMemoryContext,
										   "pg_dbbackup diff backup",
										   ALLOCSET_START_SMALL_SIZES);
		oldctx = MemoryContextSwitchTo(backup_ctx);
		bstate = palloc0_object(BackupState);
		MemoryContextSwitchTo(oldctx);

		register_persistent_abort_backup_handler();
		do_pg_backup_start("pg_dbbackup-diff", true, NULL, bstate, NULL);
		backup_started = true;

		wal_range.start = bstate->startpoint;
		wal_range.start_tli = bstate->starttli;

		collect.paths = NULL;
		collect.count = 0;
		collect.cap = 0;
		collect.base_created_at_us = base_created_at_us;

		changed.rels = NULL;
		changed.count = 0;
		changed.cap = 0;

		use_summaries = summarize_wal_enabled();

		if (use_summaries)
		{
			XLogRecPtr	cur_lsn;
			int			ret;

			SPI_connect();
			ret = SPI_execute("SELECT pg_current_wal_lsn()", true, 1);
			if (ret != SPI_OK_SELECT || SPI_processed != 1)
			{
				SPI_finish();
				ereport(ERROR,
						(errcode(ERRCODE_INTERNAL_ERROR),
						 errmsg("pg_current_wal_lsn() failed (rc=%d)", ret)));
			}
			{
				bool		isnull;
				Datum		d;

				d = SPI_getbinval(SPI_tuptable->vals[0],
								   SPI_tuptable->tupdesc, 1, &isnull);
				cur_lsn = DatumGetLSN(d);
			}
			SPI_finish();

			wait_for_summarizer(cur_lsn);
			collect_wal_summary_changes(db_oid, base_stop_lsn, cur_lsn, &changed);

			ereport(NOTICE,
					(errmsg("FULL DIFFERENTIAL via WAL summaries: %u changed (relfilenode, fork) pairs",
							changed.count)));

			for (j = 0; j < changed.count; j++)
			{
				if (changed.rels[j].limit_all)
					append_whole_relfork(&collect, db_oid,
										  changed.rels[j].relfilenode,
										  changed.rels[j].forknum);
			}
		}
		else
		{
			char		basedir[MAXPGPATH];

			ereport(NOTICE,
					(errmsg("summarize_wal=off — using mtime-based DIFF (coarser)")));

			snprintf(basedir, sizeof(basedir), "%s/base/%u",
					 DataDir, db_oid);
			if (fileio_exists(basedir))
			{
				char		relprefix[MAXPGPATH];

				snprintf(relprefix, sizeof(relprefix), "base/%u", db_oid);
				fileio_visit_files(basedir, relprefix, collect_diff_path,
									&collect);
			}
		}

		metadata_sql = metadata_gen_all(db_oid);

		memset(&header, 0, sizeof(header));
		header.format_version = BAKFILE_VERSION;
		header.mode = BACKUP_MODE_FULL;
		header.type = BACKUP_TYPE_DIFFERENTIAL;
		strlcpy(header.db_name, db_name, sizeof(header.db_name));
		header.db_oid = db_oid;
		header.start_lsn = (uint64) bstate->startpoint;
		header.stop_lsn = (uint64) bstate->startpoint;
		header.start_tli = bstate->starttli;
		header.stop_tli = bstate->starttli;
		header.pg_version = PG_VERSION_NUM;
		header.created_at = GetCurrentTimestamp();
		header.base_backup_lsn = (uint64) base_stop_lsn;
		header.compressed = compress;
		header.encrypted = (password != NULL);
		/* Count block-level entries from per-relation changed-block sets. */
		if (use_summaries)
		{
			for (j = 0; j < changed.count; j++)
			{
				if (!changed.rels[j].limit_all)
					block_entries += changed.rels[j].nblocks;
			}
		}

		header.file_count = collect.count + block_entries;
		capture_db_xid_bounds(db_oid, &header.frozen_xid, &header.min_mxid);

		writer = bakfile_create(filepath, &header, compress, password);

		bakfile_begin_section(writer, BAKSECTION_METADATA);
		if (metadata_sql && metadata_sql->len > 0)
			bakfile_write_section_data(writer, metadata_sql->data,
									   metadata_sql->len);
		bakfile_end_section(writer);

		ereport(NOTICE,
				(errmsg("FULL DIFFERENTIAL backup of \"%s\": %u whole files + %u changed blocks",
						db_name, collect.count, block_entries)));

		bakfile_begin_section(writer, BAKSECTION_DATA);
		{
			uint32		total = collect.count + block_entries;
			uint32		net_count = pg_hton32(total);

			bakfile_write_section_data(writer, &net_count, sizeof(net_count));
		}

		for (j = 0; j < collect.count; j++)
		{
			CHECK_FOR_INTERRUPTS();
			ereport(NOTICE,
					(errmsg("backing up file %u/%u: %s",
							j + 1, collect.count,
							collect.paths[j].relpath)));
			write_file_as_entry(writer, collect.paths[j].relpath,
								 collect.paths[j].abspath);
		}

		if (use_summaries && block_entries > 0)
		{
			uint32		written = 0;
			uint32		ri;

			for (ri = 0; ri < changed.count; ri++)
			{
				ChangedRel *r = &changed.rels[ri];
				uint32		bi;

				if (r->limit_all)
					continue;

				for (bi = 0; bi < r->nblocks; bi++)
				{
					CHECK_FOR_INTERRUPTS();
					if (write_block_entry(writer, db_oid, r->relfilenode,
										   r->forknum, r->blocks[bi]))
						written++;
				}
			}

			/*
			 * write_block_entry may have skipped a block if the segment file
			 * disappeared (e.g. truncation). Rewrite the entry count to match
			 * what was actually written.
			 */
			if (written != block_entries)
			{
				uint32		total = collect.count + written;
				uint32		net_count = pg_hton32(total);

				Assert(writer->section_buf != NULL);
				Assert(writer->section_buf->len >= sizeof(net_count));
				memcpy(writer->section_buf->data, &net_count, sizeof(net_count));
				block_entries = written;
				header.file_count = collect.count + block_entries;
			}
		}

		bakfile_end_section(writer);

		do_pg_backup_stop(bstate, true);
		backup_started = false;

		wal_range.stop = bstate->stoppoint;
		wal_range.stop_tli = bstate->stoptli;

		bakfile_begin_section(writer, BAKSECTION_WAL);
		if (wal_range.stop > base_stop_lsn)
		{
			WalFilterState *filter;
			XLogRecPtr	wal_start = base_stop_lsn;

			ereport(NOTICE,
					(errmsg("extracting WAL [%X/%X, %X/%X] (%lu bytes)",
							LSN_FORMAT_ARGS(wal_start),
							LSN_FORMAT_ARGS(wal_range.stop),
							(unsigned long) (wal_range.stop - wal_start))));

			filter = wal_filter_init(db_oid, wal_start,
									  wal_range.stop, wal_range.stop_tli);
			wal_filter_extract(filter, writer);
			wal_filter_free(filter);
		}
		bakfile_end_section(writer);

		header.stop_lsn = (uint64) wal_range.stop;
		header.stop_tli = wal_range.stop_tli;
		bakfile_rewrite_header(writer, &header);

		bakfile_close(writer);

		MemoryContextDelete(backup_ctx);
	}
	PG_CATCH();
	{
		if (backup_started)
		{
			PG_TRY();
			{
				do_pg_backup_stop(bstate, false);
			}
			PG_CATCH();
			{
				FlushErrorState();
			}
			PG_END_TRY();
		}

		PG_TRY();
		{
			drop_replication_slot_if_exists(slot_name);
		}
		PG_CATCH();
		{
			FlushErrorState();
		}
		PG_END_TRY();

		UnlockSharedObject(DatabaseRelationId, db_oid, 0, AccessShareLock);
		PG_RE_THROW();
	}
	PG_END_TRY();

	drop_replication_slot_if_exists(slot_name);
	pfree(slot_name);

	UnlockSharedObject(DatabaseRelationId, db_oid, 0, AccessShareLock);

	ereport(NOTICE,
			(errmsg("FULL DIFFERENTIAL backup of \"%s\" complete: written to \"%s\"",
					db_name, filepath)));
}

static XLogRecPtr
read_prev_stop_lsn(const char *prev_filepath, const char *password,
					const char *expected_db_name)
{
	BakFileReader *reader;
	XLogRecPtr	prev_stop;

	reader = bakfile_open(prev_filepath, password);

	if (reader->header.mode != BACKUP_MODE_FULL)
	{
		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("previous backup \"%s\" is not FULL mode",
						prev_filepath)));
	}

	if (strcmp(reader->header.db_name, expected_db_name) != 0)
	{
		char	   *got = pstrdup(reader->header.db_name);

		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("previous backup is for db \"%s\", not \"%s\"",
						got, expected_db_name)));
	}

	prev_stop = (XLogRecPtr) reader->header.stop_lsn;
	bakfile_close_reader(reader);
	return prev_stop;
}

void
backup_full_log(Oid db_oid, const char *db_name, const char *filepath,
				const char *prev_filepath, bool compress, const char *password)
{
	BakFileHeader header;
	BakFileWriter *writer;
	StringInfo	metadata_sql;
	FILE	   *probe;
	XLogRecPtr	prev_stop_lsn;
	XLogRecPtr	current_lsn;
	TimeLineID	current_tli;
	int			ret;

	if (!superuser())
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
				 errmsg("must be superuser to perform backups")));

	if (prev_filepath == NULL || prev_filepath[0] == '\0')
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("FULL log backup requires base_filepath (the previous .bak)"),
				 errhint("Pass the most recent FULL/DIFF/LOG .bak via base_filepath := ...")));

	prev_stop_lsn = read_prev_stop_lsn(prev_filepath, password, db_name);

	probe = AllocateFile(filepath, "wb");
	if (probe == NULL)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not open backup destination \"%s\": %m",
						filepath)));
	FreeFile(probe);
	(void) unlink(filepath);

	LockSharedObject(DatabaseRelationId, db_oid, 0, AccessShareLock);

	PG_TRY();
	{
		SPI_connect();
		ret = SPI_execute("SELECT pg_switch_wal()", false, 1);
		SPI_finish();
		if (ret != SPI_OK_SELECT)
			ereport(ERROR,
					(errcode(ERRCODE_INTERNAL_ERROR),
					 errmsg("pg_switch_wal() failed (rc=%d)", ret)));

		SPI_connect();
		ret = SPI_execute("SELECT pg_current_wal_lsn()::text", true, 1);
		if (ret != SPI_OK_SELECT || SPI_processed != 1)
		{
			SPI_finish();
			ereport(ERROR,
					(errcode(ERRCODE_INTERNAL_ERROR),
					 errmsg("pg_current_wal_lsn() failed (rc=%d)", ret)));
		}
		{
			char	   *s = SPI_getvalue(SPI_tuptable->vals[0],
										  SPI_tuptable->tupdesc, 1);
			uint32		hi,
						lo;

			if (s == NULL || sscanf(s, "%X/%X", &hi, &lo) != 2)
			{
				SPI_finish();
				ereport(ERROR,
						(errcode(ERRCODE_DATA_CORRUPTED),
						 errmsg("could not parse current WAL LSN")));
			}
			current_lsn = ((XLogRecPtr) hi << 32) | lo;
		}
		SPI_finish();

		current_tli = GetWALInsertionTimeLine();

		if (current_lsn < prev_stop_lsn)
			current_lsn = prev_stop_lsn;

		metadata_sql = metadata_gen_all(db_oid);

		memset(&header, 0, sizeof(header));
		header.format_version = BAKFILE_VERSION;
		header.mode = BACKUP_MODE_FULL;
		header.type = BACKUP_TYPE_LOG;
		strlcpy(header.db_name, db_name, sizeof(header.db_name));
		header.db_oid = db_oid;
		header.start_lsn = (uint64) prev_stop_lsn;
		header.stop_lsn = (uint64) current_lsn;
		header.start_tli = current_tli;
		header.stop_tli = current_tli;
		header.pg_version = PG_VERSION_NUM;
		header.created_at = GetCurrentTimestamp();
		header.base_backup_lsn = (uint64) prev_stop_lsn;
		header.compressed = compress;
		header.encrypted = (password != NULL);
		header.file_count = 0;
		capture_db_xid_bounds(db_oid, &header.frozen_xid, &header.min_mxid);

		writer = bakfile_create(filepath, &header, compress, password);

		bakfile_begin_section(writer, BAKSECTION_METADATA);
		if (metadata_sql && metadata_sql->len > 0)
			bakfile_write_section_data(writer, metadata_sql->data,
									   metadata_sql->len);
		bakfile_end_section(writer);

		bakfile_begin_section(writer, BAKSECTION_WAL);
		if (current_lsn > prev_stop_lsn)
		{
			WalFilterState *filter;

			ereport(NOTICE,
					(errmsg("extracting WAL [%X/%X, %X/%X] (%lu bytes)",
							LSN_FORMAT_ARGS(prev_stop_lsn),
							LSN_FORMAT_ARGS(current_lsn),
							(unsigned long) (current_lsn - prev_stop_lsn))));

			filter = wal_filter_init(db_oid, prev_stop_lsn, current_lsn,
									  current_tli);
			wal_filter_extract(filter, writer);
			wal_filter_free(filter);
		}
		bakfile_end_section(writer);

		bakfile_close(writer);
	}
	PG_CATCH();
	{
		UnlockSharedObject(DatabaseRelationId, db_oid, 0, AccessShareLock);
		PG_RE_THROW();
	}
	PG_END_TRY();

	UnlockSharedObject(DatabaseRelationId, db_oid, 0, AccessShareLock);

	ereport(NOTICE,
			(errmsg("FULL LOG backup of \"%s\" complete: WAL [%X/%X, %X/%X] written to \"%s\"",
					db_name,
					LSN_FORMAT_ARGS(prev_stop_lsn),
					LSN_FORMAT_ARGS(current_lsn),
					filepath)));
}
