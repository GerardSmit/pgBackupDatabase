/*-------------------------------------------------------------------------
 *
 * wal_replay.c
 *		Scan + classify captured WAL frames; detect PITR cutoff.
 *
 * WHAT THIS MODULE DOES
 * ---------------------
 *   1. Walks the captured WAL stream from a .bak's WAL section
 *      (`[uint32 BE xl_tot_len][xl_tot_len raw record bytes]`).
 *   2. Classifies each record by its block refs: target-db / cluster-wide
 *      rmgr / other-db. Counters surface via NOTICE for diagnostics.
 *   3. Detects the PITR cutoff: stops at the first XACT_COMMIT or
 *      XACT_COMMIT_PREPARED record whose `xact_time > stop_at`.
 *
 * WHAT THIS MODULE DOES NOT DO
 * ----------------------------
 * Records are NOT applied to the cluster. There is no in-process redo
 * pass and no synthesised single-user postgres subprocess. See WAL-REPLAY
 * NOTES below for why; this is a deliberate design choice, not a TODO.
 *
 * For same-cluster restores (the common case for pg_dbbackup) this is
 * sufficient: the captured page images in the DATA section reference
 * XIDs that already exist in the live cluster's CLOG, so all pre-backup
 * commits remain visible after the temp DB rename, no replay required.
 *
 * For inter-cluster restores or for true point-in-time recovery (data
 * committed after `do_pg_backup_start`, restored with `stop_at` between
 * backups in a FULL → LOG chain), replay is necessary and is NOT
 * available in this version. The PITR cutoff is still reported so
 * callers can detect the case.
 *
 * WAL-REPLAY NOTES (why no apply)
 * -------------------------------
 * PostgreSQL's redo path was designed for crash/standby recovery and is
 * tightly coupled to `InRecovery=true` and to having complete, contiguous
 * WAL on disk. The blockers are architectural, not incidental:
 *
 *   - XLogReadBufferExtended() asserts `InRecovery` when it has to extend
 *     a relation. Flipping the global from a live backend corrupts CLOG,
 *     MultiXact, CommitTs and the buffer manager's state.
 *   - The WAL records we capture are pre-filtered by dbOid. Real WAL is
 *     contiguous; ours has gaps. PG's redo machinery iterates by LSN and
 *     cannot consume a gap-filled stream.
 *   - The captured records reference XIDs and LSNs from the source
 *     cluster's lifetime. When restoring across clusters, those XIDs do
 *     not exist in the target's CLOG.
 *   - Reconstructing valid WAL segment files (16 MB pages, XLog page
 *     headers, sysid binding, `xlp_pageaddr` chains, timeline math,
 *     pg_control matching) from filtered records requires synthesising
 *     LSN-preserving padding for every dropped record. That is a
 *     non-trivial WAL writer in its own right.
 *
 * VIABLE FUTURE PATHS
 * -------------------
 *   * Subprocess approach: `initdb` a private PGDATA, drop in the
 *     captured `base/<src>/` files, rebuild a self-consistent WAL stream
 *     (one segment per chain entry, gap padding for filtered records),
 *     write a `backup_label` + `recovery.signal`, run `postgres
 *     --single -D <dir>` with `recovery_target_time`, then copy the
 *     recovered relfiles back into the running cluster's
 *     `base/<temp_dboid>/`. Order of magnitude: weeks of focused work
 *     centred on the WAL-segment rebuilder.
 *   * Stop filtering at backup time and record the source's pg_control
 *     + every byte of the source's `pg_wal/` window. Larger backups,
 *     identical replay story to PG's basebackup tooling.
 *   * Move WAL filtering downstream: keep full segments in the .bak and
 *     filter only at restore time, after replay against a sandbox.
 *
 * For this release we choose correctness over completeness: replay is
 * deferred and the limitation is documented. The same-cluster round-trip
 * works without VACUUM FREEZE (the cluster's CLOG keeps source XIDs
 * visible). PITR within a single FULL backup's window is reported via
 * the cutoff scan; full chain-spanning PITR is gated on the work above.
 *
 *-------------------------------------------------------------------------
 */

#include "postgres.h"

#include "access/rmgr.h"
#include "access/xact.h"
#include "access/xlog.h"
#include "access/xlog_internal.h"
#include "access/xlogreader.h"
#include "access/xlogrecord.h"
#include "miscadmin.h"
#include "port/pg_bswap.h"
#include "storage/relfilelocator.h"
#include "utils/memutils.h"

#include "bakfile.h"
#include "wal_replay.h"

/*
 * Origin ID size. PG names this typedef differently across versions
 * (RepOriginId in PG17, ReplOriginId in PG18+) and we don't actually
 * need the typedef itself — only the size. uint16 is the on-disk size.
 */
#define WAL_REPLAY_ORIGIN_ID_SIZE	sizeof(uint16)

WalReplayState *
wal_replay_init(Oid src_dboid, Oid tgt_dboid)
{
	WalReplayState *state = palloc0(sizeof(WalReplayState));

	state->src_dboid = src_dboid;
	state->tgt_dboid = tgt_dboid;
	state->last_scanned_lsn = InvalidXLogRecPtr;
	state->stopped_for_pitr = false;
	return state;
}

void
wal_replay_free(WalReplayState *state)
{
	if (state == NULL)
		return;
	pfree(state);
}

/*
 * Walk the headers section of a raw XLogRecord and return:
 *   - whether any block ref targets src_dboid (return value)
 *   - where the main_data starts inside record_bytes (`*main_data_out`,
 *     NULL if absent)
 *   - main_data length (`*main_data_len_out`)
 *
 * The on-disk layout after SizeOfXLogRecord matches what DecodeXLogRecord
 * consumes in src/backend/access/transam/xlogreader.c:1700+.
 *
 * If we can't make sense of the record (truncated, unknown block_id), we
 * stop early and return whatever we found — we don't ERROR since we're
 * only trying to compute a best-effort touches_src_db answer plus a
 * pointer to the main data for commit-time extraction.
 */
static bool
scan_record_headers(const char *record_bytes, uint32 xl_tot_len,
					 Oid src_dboid,
					 const char **main_data_out, uint32 *main_data_len_out)
{
	const char *ptr = record_bytes + SizeOfXLogRecord;
	const char *end = record_bytes + xl_tot_len;
	RelFileLocator last_rlocator = {0};
	bool		has_last_rlocator = false;
	bool		found = false;

	if (main_data_out)
		*main_data_out = NULL;
	if (main_data_len_out)
		*main_data_len_out = 0;

	while (ptr < end)
	{
		uint8		block_id;

		if (ptr + 1 > end)
			return found;
		block_id = (uint8) *ptr++;

		if (block_id == XLR_BLOCK_ID_DATA_SHORT)
		{
			uint8		mlen;

			if (ptr + 1 > end)
				return found;
			mlen = (uint8) *ptr++;
			if (main_data_out)
			{
				/*
				 * Note: per DecodeXLogRecord layout, main data is at the
				 * very tail of the record (after all block images / block
				 * data). We can't return a precise pointer without also
				 * walking block payloads; for commit records there are
				 * typically zero block refs, so end - mlen is exact. For
				 * safety we return end - mlen and let the caller validate.
				 */
				if (end - record_bytes >= (ptrdiff_t) mlen)
					*main_data_out = end - mlen;
			}
			if (main_data_len_out)
				*main_data_len_out = mlen;
			return found;
		}
		if (block_id == XLR_BLOCK_ID_DATA_LONG)
		{
			uint32		mlen;

			if (ptr + sizeof(uint32) > end)
				return found;
			memcpy(&mlen, ptr, sizeof(uint32));
			ptr += sizeof(uint32);
			if (main_data_out)
			{
				if (end - record_bytes >= (ptrdiff_t) mlen)
					*main_data_out = end - mlen;
			}
			if (main_data_len_out)
				*main_data_len_out = mlen;
			return found;
		}
		if (block_id == XLR_BLOCK_ID_ORIGIN)
		{
			if (ptr + WAL_REPLAY_ORIGIN_ID_SIZE > end)
				return found;
			ptr += WAL_REPLAY_ORIGIN_ID_SIZE;
			continue;
		}
		if (block_id == XLR_BLOCK_ID_TOPLEVEL_XID)
		{
			if (ptr + sizeof(TransactionId) > end)
				return found;
			ptr += sizeof(TransactionId);
			continue;
		}

		if (block_id > XLR_MAX_BLOCK_ID)
			return found;

		/* Block reference. */
		{
			uint8		fork_flags;
			uint16		data_len;

			if (ptr + 1 > end)
				return found;
			fork_flags = (uint8) *ptr++;

			if (ptr + sizeof(uint16) > end)
				return found;
			memcpy(&data_len, ptr, sizeof(uint16));
			ptr += sizeof(uint16);
			(void) data_len;

			if (fork_flags & BKPBLOCK_HAS_IMAGE)
			{
				uint16		bimg_len;
				uint16		hole_offset;
				uint8		bimg_info;

				if (ptr + sizeof(uint16) + sizeof(uint16) + sizeof(uint8) > end)
					return found;
				memcpy(&bimg_len, ptr, sizeof(uint16));
				ptr += sizeof(uint16);
				memcpy(&hole_offset, ptr, sizeof(uint16));
				ptr += sizeof(uint16);
				bimg_info = (uint8) *ptr++;

				(void) bimg_len;
				(void) hole_offset;

				if ((bimg_info & BKPIMAGE_HAS_HOLE) &&
					BKPIMAGE_COMPRESSED(bimg_info))
				{
					if (ptr + sizeof(uint16) > end)
						return found;
					ptr += sizeof(uint16);
				}
			}

			if (!(fork_flags & BKPBLOCK_SAME_REL))
			{
				if (ptr + sizeof(RelFileLocator) > end)
					return found;
				memcpy(&last_rlocator, ptr, sizeof(RelFileLocator));
				ptr += sizeof(RelFileLocator);
				has_last_rlocator = true;
			}

			if (ptr + sizeof(BlockNumber) > end)
				return found;
			ptr += sizeof(BlockNumber);

			if (has_last_rlocator && last_rlocator.dbOid == src_dboid)
				found = true;
		}
	}

	return found;
}

uint64
wal_replay_scan_bak(WalReplayState *state, const char *bak_filepath,
					 const char *password, bool has_stop_at,
					 TimestampTz stop_at)
{
	BakFileReader *reader;
	uint8		stype;
	uint64		count = 0;
	uint64		remaining;

	reader = bakfile_open(bak_filepath, password);

	/* Walk sections until we hit WAL. */
	for (;;)
	{
		stype = bakfile_next_section(reader);
		if (stype == 0)
		{
			bakfile_close_reader(reader);
			return count;
		}
		if (stype == BAKSECTION_WAL)
			break;
	}

	remaining = bakfile_section_remaining(reader);

	while (remaining > 0)
	{
		uint32		len_be;
		uint32		rec_len;
		char	   *rec_buf;
		XLogRecord *xlrec;
		RmgrId		rmid;
		uint8		info;
		bool		is_cluster_rmgr;
		bool		touches_src_db;
		const char *main_data = NULL;
		uint32		main_data_len = 0;

		CHECK_FOR_INTERRUPTS();

		if (remaining < sizeof(uint32))
			ereport(ERROR,
					(errcode(ERRCODE_DATA_CORRUPTED),
					 errmsg("WAL section truncated mid-length-prefix")));

		if (bakfile_read_section_data(reader, &len_be, sizeof(len_be)) !=
			sizeof(len_be))
			ereport(ERROR,
					(errcode(ERRCODE_DATA_CORRUPTED),
					 errmsg("could not read WAL record length prefix")));
		rec_len = pg_ntoh32(len_be);
		remaining -= sizeof(uint32);

		if (rec_len < SizeOfXLogRecord || rec_len > remaining)
			ereport(ERROR,
					(errcode(ERRCODE_DATA_CORRUPTED),
					 errmsg("WAL record length %u out of bounds (remaining=%lu)",
							rec_len, (unsigned long) remaining)));

		rec_buf = palloc(rec_len);
		if (bakfile_read_section_data(reader, rec_buf, rec_len) != rec_len)
			ereport(ERROR,
					(errcode(ERRCODE_DATA_CORRUPTED),
					 errmsg("could not read WAL record body of %u bytes",
							rec_len)));
		remaining -= rec_len;

		xlrec = (XLogRecord *) rec_buf;
		rmid = xlrec->xl_rmid;
		info = xlrec->xl_info & XLR_RMGR_INFO_MASK;

		is_cluster_rmgr =
			(rmid == RM_XLOG_ID || rmid == RM_XACT_ID || rmid == RM_CLOG_ID ||
			 rmid == RM_MULTIXACT_ID || rmid == RM_STANDBY_ID ||
			 rmid == RM_DBASE_ID || rmid == RM_RELMAP_ID);

		state->records_total++;

		touches_src_db = scan_record_headers(rec_buf, rec_len,
											  state->src_dboid,
											  &main_data, &main_data_len);

		if (is_cluster_rmgr)
			state->records_cluster_wide++;
		else if (touches_src_db)
			state->records_target_db++;
		else
			state->records_other_db++;

		/*
		 * PITR cutoff: stop at first commit-style XACT record whose
		 * xact_time > stop_at.
		 */
		if (has_stop_at && rmid == RM_XACT_ID)
		{
			uint8		opmask = info & XLOG_XACT_OPMASK;

			if (opmask == XLOG_XACT_COMMIT ||
				opmask == XLOG_XACT_COMMIT_PREPARED)
			{
				if (main_data != NULL &&
					main_data_len >= sizeof(TimestampTz))
				{
					TimestampTz xact_time;

					memcpy(&xact_time, main_data, sizeof(TimestampTz));
					state->commits_seen++;
					if (xact_time > stop_at)
					{
						state->stopped_for_pitr = true;
						pfree(rec_buf);
						break;
					}
				}
			}
		}

		count++;
		pfree(rec_buf);
	}

	bakfile_close_reader(reader);
	return count;
}
