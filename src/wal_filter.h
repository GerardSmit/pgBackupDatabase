#ifndef WAL_FILTER_H
#define WAL_FILTER_H

#include "postgres.h"
#include "access/xlogdefs.h"
#include "access/xlogreader.h"

struct BakFileWriter;

typedef struct WalFilterState
{
	Oid			target_db_oid;
	XLogRecPtr	start_lsn;
	XLogRecPtr	end_lsn;
	TimeLineID	tli;
	uint64		records_included;
	uint64		records_excluded;
	uint64		bytes_written;

	/* internal */
	XLogReaderState *reader;
	void	   *reader_private;
} WalFilterState;

extern WalFilterState *wal_filter_init(Oid target_db_oid,
									   XLogRecPtr start_lsn,
									   XLogRecPtr end_lsn,
									   TimeLineID tli);
extern bool wal_filter_should_include(XLogReaderState *xlogreader,
									   Oid target_db_oid);
extern void wal_filter_extract(WalFilterState *state,
							   struct BakFileWriter *writer);
extern void wal_filter_free(WalFilterState *state);

/*
 * Copy raw pg_wal segment files overlapping [start_lsn, end_lsn] into a
 * BAKSECTION_WAL_SEGMENTS section as DATA entries. Each entry's path is the
 * segment filename (e.g. "000000010000000000000003"); its data is the full
 * 16MB segment bytes. Returns the number of segments written.
 */
extern uint64 wal_segments_capture(struct BakFileWriter *writer,
								   XLogRecPtr start_lsn,
								   XLogRecPtr end_lsn,
								   TimeLineID tli);

#endif
