EXTENSION = pg_dbbackup
EXTVERSION = 1.0.0
DATA = sql/pg_dbbackup--1.0.0.sql

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
SHLIB_LINK += -lzstd -lcrypto -lcurl -L$(shell $(PG_CONFIG) --libdir) -lpq

PG_CONFIG ?= pg_config
PGXS := $(shell $(PG_CONFIG) --pgxs)
include $(PGXS)
