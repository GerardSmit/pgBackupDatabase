#include "postgres.h"

#include <unistd.h>
#include <fcntl.h>
#include <sys/stat.h>

#include "access/rmgr.h"
#include "access/xlog.h"
#include "access/xlog_internal.h"
#include "access/xlogdefs.h"
#include "access/xlogreader.h"
#include "access/xlogrecord.h"
#include "access/xlogutils.h"
#include "miscadmin.h"
#include "port/pg_bswap.h"
#include "storage/fd.h"
#include "storage/relfilelocator.h"
#include "utils/wait_event.h"

#include "bakfile.h"
#include "wal_filter.h"

WalFilterState *
wal_filter_init(Oid target_db_oid, XLogRecPtr start_lsn, XLogRecPtr end_lsn,
				TimeLineID tli)
{
	WalFilterState *state = palloc0(sizeof(WalFilterState));

	state->target_db_oid = target_db_oid;
	state->start_lsn = start_lsn;
	state->end_lsn = end_lsn;
	state->tli = tli;
	return state;
}

bool
wal_filter_should_include(XLogReaderState *xlogreader, Oid target_db_oid)
{
	RmgrId		rmid = XLogRecGetRmid(xlogreader);
	int			max_block_id;

	if (rmid == RM_XLOG_ID || rmid == RM_XACT_ID || rmid == RM_CLOG_ID ||
		rmid == RM_MULTIXACT_ID || rmid == RM_STANDBY_ID ||
		rmid == RM_DBASE_ID || rmid == RM_RELMAP_ID)
		return true;

	max_block_id = XLogRecMaxBlockId(xlogreader);
	for (int i = 0; i <= max_block_id; i++)
	{
		RelFileLocator rlocator;

		if (!XLogRecGetBlockTagExtended(xlogreader, i, &rlocator, NULL, NULL,
										NULL))
			continue;

		if (rlocator.dbOid == target_db_oid)
			return true;
	}

	return false;
}

/*
 * Read `total_len` raw record bytes starting at `start_lsn` from pg_wal/,
 * stripping per-page headers. Returns a palloc'd buffer.
 */
static char *
read_raw_record_bytes(XLogRecPtr start_lsn, uint32 total_len, TimeLineID tli)
{
	char	   *buf = palloc(total_len);
	uint32		written = 0;
	XLogRecPtr	cur = start_lsn;
	int			seg_size = wal_segment_size;
	int			seg_fd = -1;
	XLogSegNo	open_segno = (XLogSegNo) -1;
	char		segpath[MAXPGPATH];

	while (written < total_len)
	{
		uint32		page_off;
		uint32		page_room;
		uint32		want;
		XLogSegNo	segno;
		uint32		seg_off;

		CHECK_FOR_INTERRUPTS();

		XLByteToSeg(cur, segno, seg_size);

		if (segno != open_segno)
		{
			char		fname[MAXFNAMELEN];

			if (seg_fd >= 0)
				close(seg_fd);
			XLogFileName(fname, tli, segno, seg_size);
			snprintf(segpath, sizeof(segpath), "%s/" XLOGDIR "/%s",
					 DataDir, fname);
			seg_fd = BasicOpenFile(segpath, O_RDONLY | PG_BINARY);
			if (seg_fd < 0)
				ereport(ERROR,
						(errcode_for_file_access(),
						 errmsg("could not open WAL segment \"%s\": %m",
								segpath)));
			open_segno = segno;
		}

		page_off = (uint32) (cur % XLOG_BLCKSZ);

		/*
		 * If we land exactly at a page boundary mid-record, skip the page
		 * header. Records always start past the page header on their first
		 * page, so written>0 means we've just crossed into a new page.
		 */
		if (written > 0 && page_off == 0)
		{
			char		hdrbuf[SizeOfXLogLongPHD];
			ssize_t		nread;
			XLogPageHeader phdr;
			uint32		hsz;

			seg_off = (uint32) XLogSegmentOffset(cur, seg_size);
			nread = pg_pread(seg_fd, hdrbuf, SizeOfXLogLongPHD, seg_off);
			if (nread != SizeOfXLogLongPHD)
				ereport(ERROR,
						(errcode_for_file_access(),
						 errmsg("could not read WAL page header from \"%s\": %m",
								segpath)));
			phdr = (XLogPageHeader) hdrbuf;
			hsz = XLogPageHeaderSize(phdr);
			cur += hsz;
			page_off = hsz;
		}

		page_room = XLOG_BLCKSZ - page_off;
		want = total_len - written;
		if (want > page_room)
			want = page_room;

		seg_off = (uint32) XLogSegmentOffset(cur, seg_size);
		{
			ssize_t		nread;

			nread = pg_pread(seg_fd, buf + written, want, seg_off);
			if (nread != (ssize_t) want)
				ereport(ERROR,
						(errcode_for_file_access(),
						 errmsg("could not read %u bytes from WAL segment \"%s\" at offset %u: %m",
								want, segpath, seg_off)));
		}

		written += want;
		cur += want;
	}

	if (seg_fd >= 0)
		close(seg_fd);

	return buf;
}

void
wal_filter_extract(WalFilterState *state, struct BakFileWriter *writer)
{
	XLogReaderState *reader;
	ReadLocalXLogPageNoWaitPrivate *priv;
	char	   *read_errmsg = NULL;

	if (state->start_lsn >= state->end_lsn)
		return;

	priv = palloc0(sizeof(ReadLocalXLogPageNoWaitPrivate));
	state->reader_private = priv;

	reader = XLogReaderAllocate(wal_segment_size, NULL,
								XL_ROUTINE(.page_read = &read_local_xlog_page_no_wait,
										   .segment_open = &wal_segment_open,
										   .segment_close = &wal_segment_close),
								priv);
	if (reader == NULL)
		ereport(ERROR,
				(errcode(ERRCODE_OUT_OF_MEMORY),
				 errmsg("could not allocate WAL reader")));

	state->reader = reader;

	XLogBeginRead(reader, state->start_lsn);

	for (;;)
	{
		XLogRecord *record;

		CHECK_FOR_INTERRUPTS();

		record = XLogReadRecord(reader, &read_errmsg);
		if (record == NULL)
		{
			if (priv->end_of_wal)
				break;
			if (read_errmsg != NULL)
				ereport(ERROR,
						(errcode(ERRCODE_DATA_CORRUPTED),
						 errmsg("WAL read error: %s", read_errmsg)));
			break;
		}

		if (reader->ReadRecPtr >= state->end_lsn)
			break;

		if (!wal_filter_should_include(reader, state->target_db_oid))
		{
			state->records_excluded++;
			continue;
		}

		{
			uint32		rec_len = record->xl_tot_len;
			uint32		rec_len_be = pg_hton32(rec_len);
			char	   *raw;

			raw = read_raw_record_bytes(reader->ReadRecPtr, rec_len,
										reader->seg.ws_tli);

			bakfile_write_section_data(writer, &rec_len_be, sizeof(rec_len_be));
			bakfile_write_section_data(writer, raw, rec_len);

			pfree(raw);

			state->records_included++;
			state->bytes_written += sizeof(rec_len_be) + rec_len;
		}
	}

	XLogReaderFree(reader);
	state->reader = NULL;

	pfree(priv);
	state->reader_private = NULL;
}

void
wal_filter_free(WalFilterState *state)
{
	if (state == NULL)
		return;
	if (state->reader != NULL)
		XLogReaderFree(state->reader);
	if (state->reader_private != NULL)
		pfree(state->reader_private);
	pfree(state);
}

uint64
wal_segments_capture(BakFileWriter *writer, XLogRecPtr start_lsn,
					 XLogRecPtr end_lsn, TimeLineID tli)
{
	int			seg_size = wal_segment_size;
	XLogSegNo	start_segno;
	XLogSegNo	end_segno;
	XLogSegNo	segno;
	uint64		written = 0;

	if (start_lsn >= end_lsn)
		return 0;

	XLByteToSeg(start_lsn, start_segno, seg_size);
	XLByteToPrevSeg(end_lsn, end_segno, seg_size);

	for (segno = start_segno; segno <= end_segno; segno++)
	{
		char		fname[MAXFNAMELEN];
		char		segpath[MAXPGPATH];
		FILE	   *fp;
		struct stat st;
		char		buf[64 * 1024];
		size_t		nread;
		uint64		total;

		CHECK_FOR_INTERRUPTS();

		XLogFileName(fname, tli, segno, seg_size);
		snprintf(segpath, sizeof(segpath), "%s/" XLOGDIR "/%s",
				 DataDir, fname);

		if (stat(segpath, &st) != 0)
		{
			ereport(WARNING,
					(errcode_for_file_access(),
					 errmsg("WAL segment \"%s\" not found, skipping: %m",
							segpath)));
			continue;
		}

		fp = AllocateFile(segpath, "rb");
		if (fp == NULL)
		{
			ereport(WARNING,
					(errcode_for_file_access(),
					 errmsg("could not open WAL segment \"%s\": %m", segpath)));
			continue;
		}

		bakfile_begin_data_entry(writer, fname, (uint64) st.st_size);

		total = 0;
		while (total < (uint64) st.st_size)
		{
			size_t		want = sizeof(buf);

			if ((uint64) st.st_size - total < want)
				want = (size_t) ((uint64) st.st_size - total);

			nread = fread(buf, 1, want, fp);
			if (nread == 0)
				break;
			bakfile_write_data_entry_chunk(writer, buf, nread);
			total += nread;
		}

		bakfile_end_data_entry(writer);
		FreeFile(fp);
		written++;
	}

	return written;
}
