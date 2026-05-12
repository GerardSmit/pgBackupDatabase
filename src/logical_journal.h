#ifndef LOGICAL_JOURNAL_H
#define LOGICAL_JOURNAL_H

extern void pgdb_logical_journal_init(bool loaded_by_shared_preload);
extern bool pgdb_logical_journal_loaded_by_shared_preload(void);
extern void pgdb_logical_journal_set_suppressed(bool suppressed);
extern void pgdb_logical_journal_reset_state(void);

#endif
