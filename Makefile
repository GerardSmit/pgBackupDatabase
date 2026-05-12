EXTENSION = pg_dbbackup
EXTVERSION = 0.0.1
DATA = sql/pg_dbbackup--0.0.1.sql

MODULE_big = pg_dbbackup
OBJS = \
	src/pg_dbbackup.o \
	src/bakfile.o \
	src/bakfile_crypto.o \
	src/backup_simple.o \
	src/backup_full.o \
	src/backup_async.o \
	src/scheduler.o \
	src/restore_simple.o \
	src/restore_full.o \
	src/logical_plugin.o \
	src/logical_journal.o \
	src/s3_client.o \
	src/storage.o \
	src/ddl_gen.o \
	src/metadata_gen.o \
	src/inspect.o \
	src/fileio.o \
	src/libpq_helpers.o

PG_CPPFLAGS = -I$(srcdir)/src -I$(shell $(PG_CONFIG) --includedir)

# Surface real type/format/lifetime bugs at compile time. -Wno-unused-parameter
# is needed because the fmgr entry-point signature (PG_FUNCTION_ARGS) often
# leaves fcinfo unused in trivial wrappers; -Wno-declaration-after-statement
# matches Postgres's own coding style (which mixes decls into blocks).
PG_CFLAGS += -Wall -Wextra -Wno-unused-parameter -Wno-declaration-after-statement -Werror=implicit-function-declaration

SHLIB_LINK += -lzstd -lcrypto -lcurl -L$(shell $(PG_CONFIG) --libdir) -lpq

PG_CONFIG ?= pg_config
PGXS := $(shell $(PG_CONFIG) --pgxs)
include $(PGXS)
