#ifndef METADATA_GEN_H
#define METADATA_GEN_H

#include "postgres.h"
#include "lib/stringinfo.h"

extern StringInfo metadata_gen_extensions(Oid db_oid);
extern StringInfo metadata_gen_schemas(Oid db_oid);
extern StringInfo metadata_gen_db_grants(Oid db_oid);
extern StringInfo metadata_gen_default_acls(Oid db_oid);
extern StringInfo metadata_gen_object_grants(Oid db_oid);
extern StringInfo metadata_gen_comments(Oid db_oid);
extern StringInfo metadata_gen_db_settings(Oid db_oid);
extern StringInfo metadata_gen_timescaledb(Oid db_oid);
extern StringInfo metadata_gen_all(Oid db_oid);

#endif
