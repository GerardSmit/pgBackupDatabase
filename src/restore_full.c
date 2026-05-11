#include "postgres.h"

#include <libpq-fe.h>
#include <sys/stat.h>
#include <unistd.h>
#include <dirent.h>

#include "access/xlog.h"
#include "common/relpath.h"
#include "lib/stringinfo.h"
#include "miscadmin.h"
#include "port.h"
#include "postmaster/bgwriter.h"
#include "storage/block.h"
#include "storage/bufmgr.h"
#include "storage/bufpage.h"
#include "storage/fd.h"
#include "storage/latch.h"
#include "utils/builtins.h"
#include "utils/wait_event.h"

#include "bakfile.h"
#include "libpq_helpers.h"
#include "restore_full.h"
#include "subprocess_recovery.h"
#include "wal_replay.h"

/*
 * CHECKPOINT_FAST was renamed from CHECKPOINT_IMMEDIATE in PG18; provide
 * an alias so this compiles against both PG17 and PG18.
 */
#ifndef CHECKPOINT_FAST
#define CHECKPOINT_FAST CHECKPOINT_IMMEDIATE
#endif

static Oid
libpq_query_db_oid(PGconn *conn, const char *dbname)
{
	const char *params[1] = {dbname};
	PGresult   *res;
	Oid			oid;
	char	   *val;

	res = PQexecParams(conn,
					   "SELECT oid::text FROM pg_database WHERE datname = $1",
					   1, NULL, params, NULL, NULL, 0);
	if (res == NULL || PQresultStatus(res) != PGRES_TUPLES_OK ||
		PQntuples(res) != 1)
	{
		char	   *msg = res ? pstrdup(PQerrorMessage(conn)) : pstrdup("no result");

		if (res)
			PQclear(res);
		ereport(ERROR,
				(errcode(ERRCODE_INTERNAL_ERROR),
				 errmsg("could not look up oid of \"%s\"", dbname),
				 errdetail("%s", msg)));
	}

	val = PQgetvalue(res, 0, 0);
	oid = (Oid) strtoul(val, NULL, 10);
	PQclear(res);
	return oid;
}

static void
terminate_db_connections(PGconn *admin, Oid db_oid)
{
	StringInfoData sql;

	initStringInfo(&sql);
	appendStringInfo(&sql,
					 "SELECT pg_terminate_backend(pid) "
					 "FROM pg_stat_activity "
					 "WHERE datid = %u AND pid <> pg_backend_pid()",
					 db_oid);
	pgbu_libpq_exec(admin, sql.data, "terminate temp DB connections");
	pfree(sql.data);
}

static void
clear_db_dir(const char *dirpath)
{
	DIR		   *dir;
	struct dirent *de;

	dir = AllocateDir(dirpath);
	if (dir == NULL)
		return;

	while ((de = ReadDir(dir, dirpath)) != NULL)
	{
		char	   *fullpath;

		if (strcmp(de->d_name, ".") == 0 || strcmp(de->d_name, "..") == 0)
			continue;

		fullpath = psprintf("%s/%s", dirpath, de->d_name);
		(void) unlink(fullpath);
		pfree(fullpath);
	}

	FreeDir(dir);
}

/*
 * Try to interpret a DATA entry path as a block-level entry of the form
 * "base/<src_dboid>/<relnode>:<fork>:<blockno>". On match, overlay the 8KB
 * page onto the correct segment file of base/<temp_dboid>/ and return true.
 * On non-match, return false so the caller falls back to whole-file injection.
 */
static bool
inject_block_entry(const char *relpath_src, Oid src_dboid, Oid temp_dboid,
					const char *data, size_t data_len)
{
	char		src_prefix[64];
	const char *tail;
	const char *colon1;
	const char *colon2;
	char		relnode_buf[32];
	char		fork_buf[32];
	char		block_buf[32];
	Oid			relnode;
	int			forknum = MAIN_FORKNUM;
	uint64		blockno;
	uint32		segno;
	uint32		segoff;
	off_t		seg_offset;
	char		seg_relpath[MAXPGPATH];
	char		seg_abspath[MAXPGPATH];
	FILE	   *fp;

	snprintf(src_prefix, sizeof(src_prefix), "base/%u/", src_dboid);
	if (strncmp(relpath_src, src_prefix, strlen(src_prefix)) != 0)
		return false;

	tail = relpath_src + strlen(src_prefix);

	colon1 = strchr(tail, ':');
	if (colon1 == NULL)
		return false;
	colon2 = strchr(colon1 + 1, ':');
	if (colon2 == NULL)
		return false;

	if ((size_t) (colon1 - tail) >= sizeof(relnode_buf))
		return false;
	memcpy(relnode_buf, tail, colon1 - tail);
	relnode_buf[colon1 - tail] = '\0';

	if ((size_t) (colon2 - colon1 - 1) >= sizeof(fork_buf))
		return false;
	memcpy(fork_buf, colon1 + 1, colon2 - colon1 - 1);
	fork_buf[colon2 - colon1 - 1] = '\0';

	if (strlen(colon2 + 1) >= sizeof(block_buf))
		return false;
	strcpy(block_buf, colon2 + 1);

	relnode = (Oid) strtoul(relnode_buf, NULL, 10);
	blockno = strtoull(block_buf, NULL, 10);

	if (strcmp(fork_buf, "main") == 0)
		forknum = MAIN_FORKNUM;
	else if (strcmp(fork_buf, "fsm") == 0)
		forknum = FSM_FORKNUM;
	else if (strcmp(fork_buf, "vm") == 0)
		forknum = VISIBILITYMAP_FORKNUM;
	else if (strcmp(fork_buf, "init") == 0)
		forknum = INIT_FORKNUM;
	else
		return false;

	if (data_len != BLCKSZ)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("block-level DATA entry \"%s\" has size %zu (expected %u)",
						relpath_src, data_len, (unsigned) BLCKSZ)));

	segno = (uint32) (blockno / RELSEG_SIZE);
	segoff = (uint32) (blockno % RELSEG_SIZE);
	seg_offset = (off_t) segoff * BLCKSZ;

	if (forknum == MAIN_FORKNUM)
	{
		if (segno == 0)
			snprintf(seg_relpath, sizeof(seg_relpath), "base/%u/%u",
					 temp_dboid, relnode);
		else
			snprintf(seg_relpath, sizeof(seg_relpath), "base/%u/%u.%u",
					 temp_dboid, relnode, segno);
	}
	else
	{
		if (segno == 0)
			snprintf(seg_relpath, sizeof(seg_relpath), "base/%u/%u_%s",
					 temp_dboid, relnode, fork_buf);
		else
			snprintf(seg_relpath, sizeof(seg_relpath), "base/%u/%u_%s.%u",
					 temp_dboid, relnode, fork_buf, segno);
	}

	snprintf(seg_abspath, sizeof(seg_abspath), "%s/%s", DataDir, seg_relpath);

	fp = AllocateFile(seg_abspath, "r+b");
	if (fp == NULL)
	{
		/*
		 * Target segment doesn't exist yet — the relation may have grown
		 * after the FULL was taken. Create it and pre-extend with zeros up
		 * to the target offset so the block lands at the correct LBN.
		 */
		fp = AllocateFile(seg_abspath, "w+b");
		if (fp == NULL)
			ereport(ERROR,
					(errcode_for_file_access(),
					 errmsg("could not open temp DB block file \"%s\": %m",
							seg_abspath)));
	}

	if (fseeko(fp, seg_offset, SEEK_SET) != 0)
	{
		FreeFile(fp);
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not seek to offset " INT64_FORMAT " in \"%s\": %m",
						(int64) seg_offset, seg_abspath)));
	}

	if (fwrite(data, 1, BLCKSZ, fp) != BLCKSZ)
	{
		int			save_errno = errno;

		FreeFile(fp);
		errno = save_errno;
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not write block to \"%s\": %m", seg_abspath)));
	}

	if (fflush(fp) != 0)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not flush \"%s\": %m", seg_abspath)));

	if (pg_fsync(fileno(fp)) != 0)
		ereport(WARNING,
				(errcode_for_file_access(),
				 errmsg("could not fsync \"%s\": %m", seg_abspath)));

	FreeFile(fp);
	return true;
}

/*
 * Write a single relfile to DataDir/<relpath>, where relpath uses temp_dboid
 * instead of the source's dboid. Files outside `base/<src_dboid>/` are skipped
 * (no global, no tablespaces in v1).
 */
static bool
inject_relfile(const char *relpath_src, Oid src_dboid, Oid temp_dboid,
				const char *data, size_t data_len)
{
	char		src_prefix[64];
	char		dst_relpath[MAXPGPATH];
	char		dst_abspath[MAXPGPATH];
	const char *tail;
	FILE	   *fp;

	snprintf(src_prefix, sizeof(src_prefix), "base/%u/", src_dboid);
	if (strncmp(relpath_src, src_prefix, strlen(src_prefix)) != 0)
		return false;

	tail = relpath_src + strlen(src_prefix);
	snprintf(dst_relpath, sizeof(dst_relpath), "base/%u/%s",
			 temp_dboid, tail);
	snprintf(dst_abspath, sizeof(dst_abspath), "%s/%s",
			 DataDir, dst_relpath);

	fp = AllocateFile(dst_abspath, "wb");
	if (fp == NULL)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not open temp DB file \"%s\": %m",
						dst_abspath)));

	if (data_len > 0 &&
		fwrite(data, 1, data_len, fp) != data_len)
	{
		int			save_errno = errno;

		FreeFile(fp);
		errno = save_errno;
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not write temp DB file \"%s\": %m",
						dst_abspath)));
	}

	if (fflush(fp) != 0)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not flush temp DB file \"%s\": %m",
						dst_abspath)));

	if (pg_fsync(fileno(fp)) != 0)
		ereport(WARNING,
				(errcode_for_file_access(),
				 errmsg("could not fsync \"%s\": %m", dst_abspath)));

	FreeFile(fp);
	return true;
}

typedef struct ParsedBak
{
	PgDbBackupType type;
	char	   *db_name;
	Oid			src_db_oid;
	uint32		frozen_xid;
	uint32		min_mxid;
	uint64		base_backup_lsn;
	uint64		start_lsn;
	uint64		stop_lsn;
	char	   *metadata_sql;
	uint32		entry_count;
	BakDataEntry *entries;
	char	  **entry_data;
	bool		has_data_section;
	uint64		wal_section_bytes;
} ParsedBak;

static void
load_bak(const char *filepath, const char *password, ParsedBak *out)
{
	BakFileReader *reader;
	uint8		stype;
	char	   *buf;
	size_t		buf_len;
	uint32		i;

	memset(out, 0, sizeof(*out));

	reader = bakfile_open(filepath, password);

	if (reader->header.mode != BACKUP_MODE_FULL)
	{
		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("\"%s\" is not a FULL-mode backup", filepath)));
	}

	out->type = reader->header.type;
	out->db_name = pstrdup(reader->header.db_name);
	out->src_db_oid = reader->header.db_oid;
	out->frozen_xid = reader->header.frozen_xid;
	out->min_mxid = reader->header.min_mxid;
	out->base_backup_lsn = reader->header.base_backup_lsn;
	out->start_lsn = reader->header.start_lsn;
	out->stop_lsn = reader->header.stop_lsn;

	/* METADATA */
	stype = bakfile_next_section(reader);
	if (stype != BAKSECTION_METADATA)
	{
		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("expected METADATA section, got %u", stype)));
	}
	bakfile_read_section_all(reader, &buf, &buf_len);
	out->metadata_sql = buf;

	if (out->type == BACKUP_TYPE_LOG)
	{
		out->has_data_section = false;
		out->entry_count = 0;
	}
	else
	{
		stype = bakfile_next_section(reader);
		if (stype != BAKSECTION_DATA)
		{
			bakfile_close_reader(reader);
			ereport(ERROR,
					(errcode(ERRCODE_DATA_CORRUPTED),
					 errmsg("expected DATA section, got %u", stype)));
		}
		out->has_data_section = true;

		out->entry_count = bakfile_read_data_entry_count(reader);

		if (out->entry_count > 0)
		{
			out->entries = palloc0(sizeof(BakDataEntry) * out->entry_count);
			out->entry_data = palloc0(sizeof(char *) * out->entry_count);
		}

		for (i = 0; i < out->entry_count; i++)
		{
			BakDataEntry *e = &out->entries[i];
			char	   *data;

			if (!bakfile_next_data_entry(reader, e))
				ereport(ERROR,
						(errcode(ERRCODE_DATA_CORRUPTED),
						 errmsg("DATA section ended early at entry %u of %u",
								i, out->entry_count)));

			data = palloc(e->data_len == 0 ? 1 : (size_t) e->data_len);
			if (e->data_len > 0)
				bakfile_read_data_entry_chunk(reader, data,
											   (size_t) e->data_len);

			bakfile_finish_data_entry(reader, e, data, true);
			out->entry_data[i] = data;
		}
	}

	stype = bakfile_next_section(reader);
	if (stype != BAKSECTION_WAL)
	{
		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("expected WAL section, got %u", stype)));
	}
	out->wal_section_bytes = bakfile_section_remaining(reader);

	bakfile_close_reader(reader);
}

static void
validate_chain(ParsedBak *baks, int n)
{
	int			i;
	int			diff_count = 0;
	uint64		prev_stop;

	if (n < 1)
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("empty restore chain")));

	if (baks[0].type != BACKUP_TYPE_FULL)
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("first .bak in chain must be type=full")));

	prev_stop = baks[0].stop_lsn;

	for (i = 1; i < n; i++)
	{
		if (strcmp(baks[i].db_name, baks[0].db_name) != 0)
			ereport(ERROR,
					(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
					 errmsg("chain entry %d is for db \"%s\", not \"%s\"",
							i, baks[i].db_name, baks[0].db_name)));

		if (baks[i].type == BACKUP_TYPE_DIFFERENTIAL)
		{
			diff_count++;
			if (diff_count > 1)
				ereport(ERROR,
						(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
						 errmsg("chain may contain at most one DIFFERENTIAL .bak")));
			if (i != 1)
				ereport(ERROR,
						(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
						 errmsg("DIFFERENTIAL must immediately follow the FULL .bak in the chain")));
			if (baks[i].base_backup_lsn != baks[0].stop_lsn)
				ereport(ERROR,
						(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
						 errmsg("chain LSN gap: DIFF base_backup_lsn does not match FULL stop_lsn")));
			prev_stop = baks[i].stop_lsn;
		}
		else if (baks[i].type == BACKUP_TYPE_LOG)
		{
			if (baks[i].base_backup_lsn != prev_stop)
				ereport(ERROR,
						(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
						 errmsg("chain LSN gap at file %d: LOG base_backup_lsn does not match previous stop_lsn",
								i)));
			prev_stop = baks[i].stop_lsn;
		}
		else
		{
			ereport(ERROR,
					(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
					 errmsg("chain entry %d has unexpected type for follow-up backup",
							i)));
		}
	}
}

void
restore_full(const char *target_db, char **files, int file_count,
			 TimestampTz stop_at, bool has_stop_at, const char *password)
{
	ParsedBak  *baks;
	char	   *temp_dbname;
	Oid			temp_db_oid = InvalidOid;
	char		temp_db_dir[MAXPGPATH];
	int			i,
				k;

	if (!superuser())
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
				 errmsg("must be superuser to perform restores")));

	if (file_count < 1)
		ereport(ERROR,
				(errcode(ERRCODE_INVALID_PARAMETER_VALUE),
				 errmsg("at least one .bak file required")));

	baks = palloc0(sizeof(ParsedBak) * file_count);
	for (i = 0; i < file_count; i++)
		load_bak(files[i], password, &baks[i]);

	validate_chain(baks, file_count);

	/*
	 * Subprocess-based PITR: when stop_at is requested and we have WAL
	 * segments captured, build a synthetic PGDATA, run archive recovery in a
	 * private subprocess cluster to the PITR target, pg_dump the recovered
	 * DB, then pg_restore into the live cluster. Falls back to the
	 * file-injection path on failure (existing behavior).
	 */
	if (has_stop_at)
	{
		char	   *avail_detail = NULL;

		if (subprocess_recovery_available(&avail_detail))
		{
			SubprocessRecoveryInput in;
			SubprocessRecoveryResult *res;

			memset(&in, 0, sizeof(in));
			in.file_count = file_count;
			in.bak_filepaths = files;
			in.password = password;
			in.has_stop_at = has_stop_at;
			in.stop_at = stop_at;

			ereport(NOTICE,
					(errmsg("attempting subprocess PITR recovery")));

			res = subprocess_recover_and_dump(&in);
			if (res->ok && res->dump_filepath != NULL)
			{
				char	   *restore_err = NULL;
				PGconn	   *admin;
				char	   *create_sql;
				char	   *drop_sql;

				ereport(NOTICE,
						(errmsg("subprocess recovery succeeded, applying dump to target")));

				admin = pgbu_connect_libpq("postgres");
				PG_TRY();
				{
					drop_sql = psprintf(
						"DROP DATABASE IF EXISTS %s WITH (FORCE)",
						pgbu_quote_db_identifier(target_db));
					pgbu_libpq_exec(admin, drop_sql, "DROP DATABASE target");
					pfree(drop_sql);

					create_sql = psprintf(
						"CREATE DATABASE %s",
						pgbu_quote_db_identifier(target_db));
					pgbu_libpq_exec(admin, create_sql, "CREATE DATABASE target");
					pfree(create_sql);
				}
				PG_CATCH();
				{
					PQfinish(admin);
					subprocess_recovery_cleanup(res);
					PG_RE_THROW();
				}
				PG_END_TRY();
				PQfinish(admin);

				if (!subprocess_pg_restore(res->dump_filepath, target_db,
										   &restore_err))
				{
					char	   *e = restore_err ? restore_err :
						pstrdup("pg_restore failed with no detail");

					subprocess_recovery_cleanup(res);
					ereport(ERROR,
							(errcode(ERRCODE_INTERNAL_ERROR),
							 errmsg("subprocess PITR restore failed during pg_restore"),
							 errdetail("%s", e)));
				}

				subprocess_recovery_cleanup(res);
				return;
			}
			else
			{
				char	   *err = res->error_detail ? res->error_detail :
					pstrdup("(no detail)");

				ereport(NOTICE,
						(errmsg("subprocess PITR recovery not available, using file-injection path"),
						 errdetail("%s", err)));
				subprocess_recovery_cleanup(res);
			}
		}
		else if (avail_detail != NULL)
		{
			ereport(NOTICE,
					(errmsg("subprocess PITR not available: %s; using file-injection path",
							avail_detail)));
		}
	}

	temp_dbname = pgbu_generate_temp_dbname();

	ereport(NOTICE,
			(errmsg("creating temp DB \"%s\"", temp_dbname)));

	{
		PGconn	   *admin = pgbu_connect_libpq("postgres");
		char	   *create_sql;

		PG_TRY();
		{
			create_sql = psprintf(
				"CREATE DATABASE %s TEMPLATE template0",
				pgbu_quote_db_identifier(temp_dbname));
			pgbu_libpq_exec(admin, create_sql, "CREATE DATABASE temp");
			pfree(create_sql);

			temp_db_oid = libpq_query_db_oid(admin, temp_dbname);
			if (temp_db_oid == InvalidOid)
				ereport(ERROR,
						(errcode(ERRCODE_INTERNAL_ERROR),
						 errmsg("temp DB \"%s\" has invalid oid", temp_dbname)));

			terminate_db_connections(admin, temp_db_oid);
		}
		PG_CATCH();
		{
			PQfinish(admin);
			PG_RE_THROW();
		}
		PG_END_TRY();
		PQfinish(admin);
	}

	snprintf(temp_db_dir, sizeof(temp_db_dir), "%s/base/%u",
			 DataDir, temp_db_oid);

	PG_TRY();
	{
		uint32		injected = 0;
		WalReplayState *replay;
		uint64		records_total = 0;

		/*
		 * Drop any shared buffers that CREATE DATABASE may have pinned for
		 * the temp DB before we overwrite its files on disk. Pages from
		 * template0 would otherwise mask the injected content for any
		 * backend that hits the buffer pool. Belt-and-suspenders: we also
		 * release per-backend smgr handles so the next read goes to fresh
		 * file descriptors.
		 */
		DropDatabaseBuffers(temp_db_oid);
		smgrreleaseall();

		clear_db_dir(temp_db_dir);

		ereport(NOTICE,
				(errmsg("injecting %u files into base/%u/",
						baks[0].entry_count, temp_db_oid)));

		for (i = 0; i < (int) baks[0].entry_count; i++)
		{
			BakDataEntry *e = &baks[0].entries[i];

			CHECK_FOR_INTERRUPTS();

			if (inject_relfile(e->path, baks[0].src_db_oid, temp_db_oid,
							    baks[0].entry_data[i], (size_t) e->data_len))
				injected++;
		}

		if (injected == 0)
			ereport(ERROR,
					(errcode(ERRCODE_DATA_CORRUPTED),
					 errmsg("FULL .bak contained no base/<dboid>/ entries"),
					 errdetail("src_dboid in backup header is %u, looked for base/%u/...",
								baks[0].src_db_oid, baks[0].src_db_oid)));

		for (k = 1; k < file_count; k++)
		{
			if (baks[k].type == BACKUP_TYPE_DIFFERENTIAL)
			{
				for (i = 0; i < (int) baks[k].entry_count; i++)
				{
					BakDataEntry *e = &baks[k].entries[i];

					CHECK_FOR_INTERRUPTS();

					if (inject_block_entry(e->path, baks[k].src_db_oid,
											temp_db_oid,
											baks[k].entry_data[i],
											(size_t) e->data_len))
						continue;

					(void) inject_relfile(e->path, baks[k].src_db_oid,
										   temp_db_oid,
										   baks[k].entry_data[i],
										   (size_t) e->data_len);
				}
			}
		}

		/*
		 * Scan the WAL sections in chain order. We do not apply records
		 * here — same-cluster restores are made consistent by the
		 * captured page images plus the live cluster's existing CLOG.
		 * The scan still honours the PITR cutoff so callers see a clean
		 * stop_at-aware result; see wal_replay.c for the rationale.
		 */
		replay = wal_replay_init(baks[0].src_db_oid, temp_db_oid);
		for (k = 0; k < file_count; k++)
		{
			records_total +=
				wal_replay_scan_bak(replay, files[k], password,
									 has_stop_at, stop_at);
			if (replay->stopped_for_pitr)
				break;
		}

		if (has_stop_at && !replay->stopped_for_pitr)
		{
			/*
			 * stop_at lies past every commit captured in the chain. That's
			 * fine — the restore reflects the entire chain's end state
			 * (modulo the no-replay caveat below). Emit an informational
			 * NOTICE.
			 */
			ereport(NOTICE,
					(errmsg("stop_at point lies past the last commit in the backup chain; restoring to chain end")));
		}

		if (records_total > 0)
		{
			ereport(NOTICE,
					(errmsg("scanned %lu WAL records from backup chain (no apply pass; see wal_replay.c)",
							(unsigned long) records_total),
					 errdetail("Restored data reflects the page-level state captured during do_pg_backup_start. Post-backup changes recorded in DIFF/LOG WAL sections are not yet applied.")));
		}

		wal_replay_free(replay);

		/*
		 * Final flush: ensure the temp DB's injected pages are
		 * authoritative by once again clearing any stragglers from the
		 * buffer pool, then issuing an immediate checkpoint so smgr write-
		 * back catches any incidentally-dirty pages (there should be none
		 * since we wrote files directly).
		 */
		DropDatabaseBuffers(temp_db_oid);
		smgrreleaseall();
		RequestCheckpoint(CHECKPOINT_FAST | CHECKPOINT_FORCE | CHECKPOINT_WAIT);

		{
			PGconn	   *admin = pgbu_connect_libpq("postgres");

			PG_TRY();
			{
				StringInfoData sql;

				initStringInfo(&sql);
				appendStringInfo(&sql,
								 "UPDATE pg_catalog.pg_database "
								 "SET datfrozenxid = '%u'::xid, "
								 "    datminmxid = '%u'::xid "
								 "WHERE oid = %u",
								 baks[0].frozen_xid,
								 baks[0].min_mxid,
								 temp_db_oid);
				if (baks[0].frozen_xid != 0 || baks[0].min_mxid != 0)
					pgbu_libpq_exec(admin, sql.data,
									"advance datfrozenxid/datminmxid");
				pfree(sql.data);

				terminate_db_connections(admin, temp_db_oid);
			}
			PG_CATCH();
			{
				PQfinish(admin);
				PG_RE_THROW();
			}
			PG_END_TRY();
			PQfinish(admin);
		}

		if (baks[0].metadata_sql && baks[0].metadata_sql[0])
		{
			PGconn	   *conn = NULL;

			PG_TRY();
			{
				conn = pgbu_connect_libpq(temp_dbname);
				pgbu_libpq_exec(conn, baks[0].metadata_sql, "METADATA");
			}
			PG_CATCH();
			{
				if (conn)
					PQfinish(conn);
				FlushErrorState();
				ereport(NOTICE,
						(errmsg("METADATA reapply failed against restored temp DB; "
								"continuing with data-only restore")));
				conn = NULL;
			}
			PG_END_TRY();
			if (conn)
				PQfinish(conn);
		}
	}
	PG_CATCH();
	{
		pgbu_drop_temp_db_quiet(temp_dbname);
		PG_RE_THROW();
	}
	PG_END_TRY();

	{
		PGconn	   *admin = pgbu_connect_libpq("postgres");
		char	   *drop_sql;
		char	   *rename_sql;

		PG_TRY();
		{
			drop_sql = psprintf(
				"DROP DATABASE IF EXISTS %s WITH (FORCE)",
				pgbu_quote_db_identifier(target_db));
			rename_sql = psprintf(
				"ALTER DATABASE %s RENAME TO %s",
				pgbu_quote_db_identifier(temp_dbname),
				pgbu_quote_db_identifier(target_db));

			pgbu_libpq_exec(admin, drop_sql, "DROP DATABASE target");

			ereport(NOTICE,
					(errmsg("renaming %s -> %s", temp_dbname, target_db)));

			pgbu_libpq_exec(admin, rename_sql, "ALTER DATABASE RENAME");

			pfree(drop_sql);
			pfree(rename_sql);
		}
		PG_CATCH();
		{
			PQfinish(admin);
			pgbu_drop_temp_db_quiet(temp_dbname);
			PG_RE_THROW();
		}
		PG_END_TRY();
		PQfinish(admin);
	}

	pfree(temp_dbname);
}
