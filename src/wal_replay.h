#ifndef WAL_REPLAY_H
#define WAL_REPLAY_H

#include "postgres.h"
#include "access/xlogdefs.h"
#include "datatype/timestamp.h"

/*
 * WAL scan state. Walks each .bak's WAL section, classifies records by
 * dbOid, detects the PITR cutoff. Records are not applied to the cluster;
 * see wal_replay.c for the design rationale and the prerequisites for a
 * future apply pass (subprocess + synthetic PGDATA + WAL-segment rebuild).
 *
 * Same-cluster restores do not need a replay pass: the captured page
 * images reference XIDs that remain in the live cluster's CLOG, so all
 * pre-backup commits stay visible after the temp DB rename. This module
 * exists to bookkeep records and to honour `stop_at` cutoffs.
 */
typedef struct WalReplayState
{
	Oid			src_dboid;
	Oid			tgt_dboid;

	XLogRecPtr	last_scanned_lsn;
	bool		stopped_for_pitr;

	uint64		records_total;
	uint64		records_target_db;	/* records with at least one block ref to src_dboid */
	uint64		records_cluster_wide;
	uint64		records_other_db;
	uint64		commits_seen;
} WalReplayState;

extern WalReplayState *wal_replay_init(Oid src_dboid, Oid tgt_dboid);

/*
 * Scan one .bak file's WAL section (records framed as
 * `[uint32 BE xl_tot_len][xl_tot_len raw record bytes]`).
 *
 * If has_stop_at is true and we encounter a transaction commit record
 * with `xact_time > stop_at`, scanning stops BEFORE that record and
 * `state->stopped_for_pitr` is set to true.
 *
 * Returns the number of records consumed (regardless of apply status).
 */
extern uint64 wal_replay_scan_bak(WalReplayState *state,
								  const char *bak_filepath,
								  const char *password,
								  bool has_stop_at,
								  TimestampTz stop_at);

extern void wal_replay_free(WalReplayState *state);

#endif							/* WAL_REPLAY_H */
