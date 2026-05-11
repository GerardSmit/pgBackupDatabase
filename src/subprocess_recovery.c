/*-------------------------------------------------------------------------
 *
 * subprocess_recovery.c
 *		Spin up a private PostgreSQL instance from a captured FULL .bak +
 *		WAL segment files, drive archive recovery to a PITR target, then
 *		pg_dump the recovered database. The dump is the input to a normal
 *		pg_restore call against the live cluster.
 *
 * High-level flow:
 *   1. mkdir /tmp/pg_dbbackup_recovery_<rnd>
 *   2. initdb -D <tmpdir> --auth=trust --no-locale --username=postgres
 *   3. Stop the post-initdb cluster (it's started briefly during template1
 *      setup; initdb leaves it stopped at the end).
 *   4. Replace <tmpdir>/base/<initdb_dboid>/ contents with FULL.bak's
 *      base/<src_dboid>/... DATA entries.
 *   5. Replace <tmpdir>/pg_wal/ with the WAL segment files from
 *      BAKSECTION_WAL_SEGMENTS.
 *   6. Write <tmpdir>/backup_label from the captured LSN/TLI.
 *   7. Write <tmpdir>/recovery.signal (empty).
 *   8. Write <tmpdir>/postgresql.auto.conf with recovery_target_time +
 *      recovery_target_action = promote (or 'immediate' if no PITR).
 *      Use unix_socket_directories = /tmp/<sock>; port = random.
 *   9. pg_ctl -D <tmpdir> -w start
 *  10. wait for the cluster to reach a queryable state via libpq probe.
 *  11. pg_dump -Fc -h <sock> -p <port> -d <src_db_name> -f <dumpfile>
 *  12. pg_ctl -D <tmpdir> -m fast stop
 *  13. return dumpfile path; live cluster does pg_restore.
 *
 * Caller is responsible for unlinking the dump file and calling
 * subprocess_recovery_cleanup() to rm -rf the tmpdir.
 *
 *-------------------------------------------------------------------------
 */

#include "postgres.h"

#include <sys/stat.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>
#include <fcntl.h>
#include <stdlib.h>
#include <time.h>

#include "access/xlog.h"
#include "access/xlog_internal.h"
#include "access/xlogdefs.h"
#include "lib/stringinfo.h"
#include "miscadmin.h"
#include "pgtime.h"
#include "port.h"
#include "storage/fd.h"
#include "utils/timestamp.h"
#include "utils/wait_event.h"

#include "bakfile.h"
#include "subprocess_recovery.h"

#define SUBPROC_DEFAULT_BINDIR "/usr/lib/postgresql/17/bin"

static char *
locate_binary(const char *name)
{
	char	   *paths[] = {
		"/usr/lib/postgresql/17/bin",
		"/usr/lib/postgresql/18/bin",
		"/usr/local/pgsql/bin",
		"/usr/bin",
		"/usr/local/bin",
		NULL
	};
	int			i;

	for (i = 0; paths[i] != NULL; i++)
	{
		char	   *candidate;
		struct stat st;

		candidate = psprintf("%s/%s", paths[i], name);
		if (stat(candidate, &st) == 0 && S_ISREG(st.st_mode) &&
			access(candidate, X_OK) == 0)
			return candidate;
		pfree(candidate);
	}
	return NULL;
}

bool
subprocess_recovery_available(char **detail_out)
{
	const char *needed[] = {"initdb", "pg_ctl", "pg_dump", "pg_restore", NULL};
	int			i;

	for (i = 0; needed[i] != NULL; i++)
	{
		char	   *p = locate_binary(needed[i]);

		if (p == NULL)
		{
			if (detail_out)
				*detail_out = psprintf("%s not found in any known bindir",
									   needed[i]);
			return false;
		}
		pfree(p);
	}
	if (detail_out)
		*detail_out = NULL;
	return true;
}

static int
run_command(const char *cmd, StringInfo log)
{
	FILE	   *fp;
	char		buf[4096];
	size_t		n;
	int			rc;

	fp = popen(cmd, "r");
	if (fp == NULL)
	{
		if (log)
			appendStringInfo(log, "popen(%s) failed: %m\n", cmd);
		return -1;
	}

	while ((n = fread(buf, 1, sizeof(buf) - 1, fp)) > 0)
	{
		buf[n] = '\0';
		if (log)
			appendStringInfoString(log, buf);
	}

	rc = pclose(fp);
	return rc;
}

static void
rm_rf(const char *path)
{
	char	   *cmd;

	if (path == NULL || path[0] == '\0' || strcmp(path, "/") == 0)
		return;
	cmd = psprintf("rm -rf %s", path);
	(void) run_command(cmd, NULL);
	pfree(cmd);
}

static void
ensure_dir(const char *path)
{
	if (mkdir(path, 0700) != 0 && errno != EEXIST)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not create directory \"%s\": %m", path)));
}

static char *
generate_tmpdir(void)
{
	uint64		r = ((uint64) random() << 32) | (uint64) random();

	return psprintf("/tmp/pg_dbbackup_recovery_%016lx", (unsigned long) r);
}

static int
choose_random_port(void)
{
	return 30000 + (random() % 20000);
}

static void
write_file(const char *path, const char *contents, size_t len)
{
	FILE	   *fp;

	fp = AllocateFile(path, "wb");
	if (fp == NULL)
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not create file \"%s\": %m", path)));
	if (len > 0 && fwrite(contents, 1, len, fp) != len)
	{
		FreeFile(fp);
		ereport(ERROR,
				(errcode_for_file_access(),
				 errmsg("could not write file \"%s\": %m", path)));
	}
	FreeFile(fp);
}

static Oid
find_initdb_dboid(const char *pgdata)
{
	char	   *basedir;
	DIR		   *d;
	struct dirent *de;
	Oid			found = InvalidOid;

	basedir = psprintf("%s/base", pgdata);
	d = AllocateDir(basedir);
	if (d == NULL)
	{
		pfree(basedir);
		return InvalidOid;
	}
	while ((de = ReadDir(d, basedir)) != NULL)
	{
		Oid			oid;
		char	   *end;

		if (strcmp(de->d_name, ".") == 0 || strcmp(de->d_name, "..") == 0)
			continue;
		oid = (Oid) strtoul(de->d_name, &end, 10);
		if (*end != '\0')
			continue;
		/* Pick the highest-numbered: that's typically the user-template clone. */
		if (oid > found)
			found = oid;
	}
	FreeDir(d);
	pfree(basedir);
	return found;
}

/*
 * Open the FULL .bak, walk DATA section, write each entry to
 * <pgdata>/base/<dest_dboid>/<basename> where basename is the part of the
 * captured relpath after "base/<src_dboid>/".
 */
static int
inject_base_files(const char *bak_filepath, const char *password,
				  const char *pgdata, Oid dest_dboid)
{
	BakFileReader *reader;
	uint8		stype;
	char	   *meta_buf;
	size_t		meta_len;
	uint32		count;
	uint32		i;
	Oid			src_dboid;
	char		dest_dir[MAXPGPATH];
	int			injected = 0;
	char		src_prefix[64];

	reader = bakfile_open(bak_filepath, password);
	src_dboid = reader->header.db_oid;
	snprintf(src_prefix, sizeof(src_prefix), "base/%u/", src_dboid);

	snprintf(dest_dir, sizeof(dest_dir), "%s/base/%u", pgdata, dest_dboid);

	/* Clear dest dir. */
	{
		char	   *cmd = psprintf("rm -rf %s/*", dest_dir);

		(void) run_command(cmd, NULL);
		pfree(cmd);
	}

	/* METADATA */
	stype = bakfile_next_section(reader);
	if (stype != BAKSECTION_METADATA)
	{
		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("expected METADATA section, got %u", stype)));
	}
	bakfile_read_section_all(reader, &meta_buf, &meta_len);

	/* DATA */
	stype = bakfile_next_section(reader);
	if (stype != BAKSECTION_DATA)
	{
		bakfile_close_reader(reader);
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("expected DATA section, got %u", stype)));
	}

	count = bakfile_read_data_entry_count(reader);
	for (i = 0; i < count; i++)
	{
		BakDataEntry e;
		char	   *data;
		char		dst_abs[MAXPGPATH];
		FILE	   *fp;
		const char *tail;
		bool		is_base;

		if (!bakfile_next_data_entry(reader, &e))
			break;

		data = palloc(e.data_len == 0 ? 1 : (size_t) e.data_len);
		if (e.data_len > 0)
			bakfile_read_data_entry_chunk(reader, data, (size_t) e.data_len);
		bakfile_finish_data_entry(reader, &e, data, false);

		is_base = (strncmp(e.path, src_prefix, strlen(src_prefix)) == 0);

		if (is_base)
		{
			tail = e.path + strlen(src_prefix);
			snprintf(dst_abs, sizeof(dst_abs), "%s/%s", dest_dir, tail);
		}
		else if (strcmp(e.path, "global/pg_control") == 0)
		{
			/*
			 * The source's pg_control carries the database system identifier
			 * that matches our captured WAL segments. Inject it into the
			 * synthetic cluster so the startup process accepts the WAL.
			 */
			snprintf(dst_abs, sizeof(dst_abs), "%s/global/pg_control", pgdata);
		}
		else if (strcmp(e.path, "global/pg_filenode.map") == 0)
		{
			/* Skip — synthetic cluster has its own filenode.map. */
			pfree(data);
			continue;
		}
		else
		{
			pfree(data);
			continue;
		}

		fp = AllocateFile(dst_abs, "wb");
		if (fp == NULL)
		{
			ereport(WARNING,
					(errcode_for_file_access(),
					 errmsg("could not create file \"%s\": %m", dst_abs)));
			pfree(data);
			continue;
		}
		if (e.data_len > 0 && fwrite(data, 1, (size_t) e.data_len, fp) !=
			(size_t) e.data_len)
		{
			FreeFile(fp);
			ereport(WARNING,
					(errcode_for_file_access(),
					 errmsg("could not write file \"%s\": %m", dst_abs)));
			pfree(data);
			continue;
		}
		FreeFile(fp);
		injected++;
		pfree(data);
	}

	bakfile_close_reader(reader);
	return injected;
}

/*
 * Open every .bak in the chain, walk BAKSECTION_WAL_SEGMENTS, write each
 * segment to <pgdata>/pg_wal/<filename>.
 */
static int
inject_wal_segments(int bak_count, char **bak_filepaths, const char *password,
					const char *pgdata)
{
	int			total = 0;
	int			k;
	char		pgwal[MAXPGPATH];

	snprintf(pgwal, sizeof(pgwal), "%s/pg_wal", pgdata);

	/* Wipe initdb's pg_wal (it has its own bootstrap segment we don't want). */
	{
		char	   *cmd = psprintf("rm -rf %s/* 2>/dev/null || true", pgwal);

		(void) run_command(cmd, NULL);
		pfree(cmd);
	}
	(void) mkdir(pgwal, 0700);

	for (k = 0; k < bak_count; k++)
	{
		BakFileReader *reader;
		uint8		stype;
		bool		found_segments = false;

		reader = bakfile_open(bak_filepaths[k], password);

		while (!found_segments)
		{
			stype = bakfile_next_section(reader);
			if (stype == 0)
				break;
			if (stype == BAKSECTION_WAL_SEGMENTS)
			{
				uint32		segcount;
				uint32		i;

				found_segments = true;
				segcount = bakfile_read_data_entry_count(reader);
				for (i = 0; i < segcount; i++)
				{
					BakDataEntry e;
					char	   *data;
					char		dst[MAXPGPATH];
					FILE	   *fp;

					if (!bakfile_next_data_entry(reader, &e))
						break;
					data = palloc(e.data_len == 0 ? 1 : (size_t) e.data_len);
					if (e.data_len > 0)
						bakfile_read_data_entry_chunk(reader, data,
													  (size_t) e.data_len);
					bakfile_finish_data_entry(reader, &e, data, false);

					snprintf(dst, sizeof(dst), "%s/%s", pgwal, e.path);
					fp = AllocateFile(dst, "wb");
					if (fp != NULL)
					{
						if (e.data_len > 0)
							(void) fwrite(data, 1, (size_t) e.data_len, fp);
						FreeFile(fp);
						total++;
					}
					pfree(data);
				}
			}
		}

		bakfile_close_reader(reader);
	}

	return total;
}

static void
write_backup_label(const char *pgdata, XLogRecPtr start_lsn,
				   XLogRecPtr checkpoint_lsn, TimeLineID start_tli)
{
	char		labelpath[MAXPGPATH];
	StringInfoData s;
	char		startwalfile[MAXFNAMELEN];
	XLogSegNo	segno;
	int			seg_size = 16 * 1024 * 1024;
	time_t		now = time(NULL);
	char		nowbuf[64];
	struct tm  *tm_now;

	snprintf(labelpath, sizeof(labelpath), "%s/backup_label", pgdata);

	XLByteToSeg(start_lsn, segno, seg_size);
	XLogFileName(startwalfile, start_tli, segno, seg_size);

	tm_now = gmtime(&now);
	strftime(nowbuf, sizeof(nowbuf), "%Y-%m-%d %H:%M:%S UTC", tm_now);

	initStringInfo(&s);
	appendStringInfo(&s, "START WAL LOCATION: %X/%08X (file %s)\n",
					 (uint32) (start_lsn >> 32), (uint32) start_lsn,
					 startwalfile);
	appendStringInfo(&s, "CHECKPOINT LOCATION: %X/%08X\n",
					 (uint32) (checkpoint_lsn >> 32),
					 (uint32) checkpoint_lsn);
	appendStringInfoString(&s, "BACKUP METHOD: streamed\n");
	appendStringInfoString(&s, "BACKUP FROM: primary\n");
	appendStringInfo(&s, "START TIME: %s\n", nowbuf);
	appendStringInfoString(&s, "LABEL: pg_dbbackup\n");
	appendStringInfo(&s, "START TIMELINE: %u\n", start_tli);

	write_file(labelpath, s.data, s.len);
	pfree(s.data);
}

static char *
build_postgresql_conf(int port, const char *sockdir, bool has_stop_at,
					  TimestampTz stop_at, const char *target_tli)
{
	StringInfoData s;

	initStringInfo(&s);
	appendStringInfo(&s, "port = %d\n", port);
	appendStringInfo(&s, "unix_socket_directories = '%s'\n", sockdir);
	appendStringInfoString(&s, "listen_addresses = ''\n");
	appendStringInfoString(&s, "shared_buffers = 32MB\n");
	appendStringInfoString(&s, "max_connections = 10\n");
	appendStringInfoString(&s, "fsync = off\n");
	appendStringInfoString(&s, "synchronous_commit = off\n");
	appendStringInfoString(&s, "wal_level = minimal\n");
	appendStringInfoString(&s, "max_wal_senders = 0\n");
	appendStringInfoString(&s, "logging_collector = off\n");

	/*
	 * Required for archive recovery; we placed all segments directly in
	 * pg_wal/ so a no-op restore_command is enough. Returning false signals
	 * recovery to look for the next segment in pg_wal/.
	 */
	appendStringInfoString(&s, "restore_command = '/bin/false'\n");

	if (has_stop_at)
	{
		char		tsbuf[64];
		pg_time_t	t = (pg_time_t) (stop_at / USECS_PER_SEC) +
			(POSTGRES_EPOCH_JDATE - UNIX_EPOCH_JDATE) * SECS_PER_DAY;
		struct tm  *tm_p;

		tm_p = gmtime(&t);
		strftime(tsbuf, sizeof(tsbuf), "%Y-%m-%d %H:%M:%S+00", tm_p);
		appendStringInfo(&s, "recovery_target_time = '%s'\n", tsbuf);
		appendStringInfoString(&s, "recovery_target_action = 'promote'\n");
	}
	else
	{
		appendStringInfoString(&s, "recovery_target = 'immediate'\n");
		appendStringInfoString(&s, "recovery_target_action = 'promote'\n");
	}

	(void) target_tli;

	return s.data;
}

static bool
wait_for_postgres_ready(const char *sockdir, int port, int timeout_sec,
						StringInfo log)
{
	char	   *psql;
	int			i;

	psql = locate_binary("psql");
	if (psql == NULL)
	{
		if (log)
			appendStringInfoString(log, "psql binary not found\n");
		return false;
	}

	for (i = 0; i < timeout_sec * 2; i++)
	{
		char	   *cmd;
		int			rc;

		cmd = psprintf("%s -h %s -p %d -U postgres -d postgres -tAc 'SELECT 1' "
					   ">/dev/null 2>&1", psql, sockdir, port);
		rc = run_command(cmd, NULL);
		pfree(cmd);

		if (rc == 0)
		{
			pfree(psql);
			return true;
		}
		pg_usleep(500000);
		CHECK_FOR_INTERRUPTS();
	}

	pfree(psql);
	if (log)
		appendStringInfo(log, "postgres did not become ready within %d seconds\n",
						 timeout_sec);
	return false;
}

SubprocessRecoveryResult *
subprocess_recover_and_dump(SubprocessRecoveryInput *input)
{
	SubprocessRecoveryResult *result;
	BakFileReader *reader;
	char	   *tmpdir;
	char	   *sockdir;
	char	   *initdb;
	char	   *pg_ctl;
	char	   *pg_dump;
	char	   *cmd;
	StringInfoData log;
	int			port;
	int			rc;
	Oid			dest_dboid;
	XLogRecPtr	start_lsn;
	XLogRecPtr	checkpoint_lsn;
	TimeLineID	start_tli;
	char	   *src_db_name;
	char	   *conf;
	char		confpath[MAXPGPATH];
	char		signalpath[MAXPGPATH];
	int			injected;
	int			seg_count;

	result = palloc0(sizeof(SubprocessRecoveryResult));
	initStringInfo(&log);

	initdb = locate_binary("initdb");
	pg_ctl = locate_binary("pg_ctl");
	pg_dump = locate_binary("pg_dump");
	if (initdb == NULL || pg_ctl == NULL || pg_dump == NULL)
	{
		result->error_detail =
			pstrdup("required postgres binaries not found");
		return result;
	}

	tmpdir = generate_tmpdir();
	sockdir = psprintf("%s/sock", tmpdir);
	ensure_dir(tmpdir);
	ensure_dir(sockdir);

	result->tmp_pgdata = pstrdup(tmpdir);

	port = choose_random_port();

	cmd = psprintf("%s -D %s/pgdata --auth=trust --no-locale --username=postgres "
				   "-E UTF8 --no-sync >>%s/initdb.log 2>&1",
				   initdb, tmpdir, tmpdir);
	rc = run_command(cmd, &log);
	pfree(cmd);
	if (rc != 0)
	{
		result->error_detail = psprintf("initdb failed (rc=%d): %s",
										rc, log.data);
		return result;
	}

	{
		char	   *pgdata = psprintf("%s/pgdata", tmpdir);

		/* Read FULL .bak header to learn src_db_name + LSNs. */
		reader = bakfile_open(input->bak_filepaths[0], input->password);
		start_lsn = (XLogRecPtr) reader->header.start_lsn;
		start_tli = reader->header.start_tli;
		checkpoint_lsn = (XLogRecPtr) reader->header.checkpoint_lsn;
		src_db_name = pstrdup(reader->header.db_name);
		bakfile_close_reader(reader);

		if (checkpoint_lsn == 0)
		{
			result->error_detail =
				psprintf("FULL .bak header has no checkpoint_lsn — backup was "
						 "taken with an older version that didn't capture it. "
						 "Retake the FULL backup to enable subprocess PITR.");
			pfree(pgdata);
			return result;
		}

		dest_dboid = find_initdb_dboid(pgdata);
		if (dest_dboid == InvalidOid)
		{
			result->error_detail =
				pstrdup("could not find a database oid in the synthetic PGDATA");
			pfree(pgdata);
			return result;
		}

		/*
		 * The synthetic cluster after initdb has only template0/template1/
		 * postgres. The source db_name almost certainly doesn't exist yet —
		 * but the relfilenodes in the .bak point to a specific dboid. We
		 * inject files into postgres's directory (the highest-numbered),
		 * which becomes the recovered database. After recovery completes,
		 * we'll rename pg_database.datname for that oid via SQL.
		 */
		injected = inject_base_files(input->bak_filepaths[0], input->password,
									 pgdata, dest_dboid);
		if (injected == 0)
		{
			result->error_detail =
				pstrdup("FULL .bak contained no base/<dboid>/ files to inject");
			pfree(pgdata);
			return result;
		}

		seg_count = inject_wal_segments(input->file_count, input->bak_filepaths,
										input->password, pgdata);
		if (seg_count == 0)
		{
			result->error_detail =
				pstrdup("no WAL segments in .bak chain (BAKSECTION_WAL_SEGMENTS "
						"missing); cannot do PITR. Retake the backup chain.");
			pfree(pgdata);
			return result;
		}

		write_backup_label(pgdata, start_lsn, checkpoint_lsn, start_tli);

		snprintf(signalpath, sizeof(signalpath), "%s/recovery.signal", pgdata);
		write_file(signalpath, "", 0);

		conf = build_postgresql_conf(port, sockdir,
									  input->has_stop_at, input->stop_at, NULL);
		snprintf(confpath, sizeof(confpath), "%s/postgresql.auto.conf", pgdata);
		write_file(confpath, conf, strlen(conf));
		pfree(conf);

		cmd = psprintf("%s -D %s -l %s/pg.log -w -t 60 start 2>&1",
					   pg_ctl, pgdata, tmpdir);
		{
			StringInfoData ctl_log;

			initStringInfo(&ctl_log);
			rc = run_command(cmd, &ctl_log);
			pfree(cmd);
			if (rc != 0)
			{
				char	   *pglog = psprintf("cat %s/pg.log 2>&1; ls -la %s 2>&1",
											 tmpdir, pgdata);
				StringInfoData logtail;

				initStringInfo(&logtail);
				(void) run_command(pglog, &logtail);
				pfree(pglog);

				result->error_detail =
					psprintf("pg_ctl start failed (rc=%d). ctl output:\n%s\npg log+ls:\n%s",
							 rc, ctl_log.data, logtail.data);
				pfree(logtail.data);
				pfree(ctl_log.data);
				pfree(pgdata);
				return result;
			}
			pfree(ctl_log.data);
		}

		if (!wait_for_postgres_ready(sockdir, port, 60, &log))
		{
			result->error_detail =
				psprintf("subprocess postgres did not become queryable.\nlog:\n%s",
						 log.data);
			pfree(pgdata);
			return result;
		}

		{
			char	   *dumpfile = psprintf("%s/dump.pgdump", tmpdir);
			char	   *db_to_dump = src_db_name;

			cmd = psprintf("%s -h %s -p %d -U postgres -Fc -f %s -d %s 2>&1",
						   pg_dump, sockdir, port, dumpfile, db_to_dump);
			{
				StringInfoData dumplog;

				initStringInfo(&dumplog);
				rc = run_command(cmd, &dumplog);
				pfree(cmd);
				if (rc != 0)
				{
					result->error_detail =
						psprintf("pg_dump failed (rc=%d) for db \"%s\". output:\n%s",
								 rc, db_to_dump, dumplog.data);
					pfree(dumplog.data);
					pfree(dumpfile);
					pfree(pgdata);

					cmd = psprintf("%s -D %s -m immediate stop >/dev/null 2>&1",
								   pg_ctl, pgdata);
					(void) run_command(cmd, NULL);
					pfree(cmd);
					return result;
				}
				pfree(dumplog.data);
			}

			result->dump_filepath = dumpfile;
		}

		cmd = psprintf("%s -D %s -m fast -w stop >/dev/null 2>&1",
					   pg_ctl, pgdata);
		(void) run_command(cmd, NULL);
		pfree(cmd);

		pfree(pgdata);
	}

	result->ok = true;
	pfree(log.data);
	return result;
}

void
subprocess_recovery_cleanup(SubprocessRecoveryResult *result)
{
	if (result == NULL)
		return;
	if (result->tmp_pgdata != NULL)
		rm_rf(result->tmp_pgdata);
	if (result->dump_filepath != NULL)
		pfree(result->dump_filepath);
	if (result->tmp_pgdata != NULL)
		pfree(result->tmp_pgdata);
	if (result->error_detail != NULL)
		pfree(result->error_detail);
	pfree(result);
}

bool
subprocess_pg_restore(const char *dump_filepath, const char *target_dbname,
					   char **error_detail_out)
{
	char	   *pg_restore;
	char	   *cmd;
	StringInfoData log;
	int			rc;

	pg_restore = locate_binary("pg_restore");
	if (pg_restore == NULL)
	{
		if (error_detail_out)
			*error_detail_out = pstrdup("pg_restore not found");
		return false;
	}

	initStringInfo(&log);

	cmd = psprintf("%s -h /tmp -U postgres -d %s --no-owner --no-privileges %s 2>&1",
				   pg_restore, target_dbname, dump_filepath);
	rc = run_command(cmd, &log);
	pfree(cmd);
	pfree(pg_restore);

	if (rc != 0)
	{
		if (error_detail_out)
			*error_detail_out = psprintf("pg_restore failed (rc=%d): %s",
										 rc, log.data);
		pfree(log.data);
		return false;
	}

	pfree(log.data);
	return true;
}
