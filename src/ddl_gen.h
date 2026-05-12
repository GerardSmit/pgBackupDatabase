#ifndef DDL_GEN_H
#define DDL_GEN_H

#include "postgres.h"
#include "lib/stringinfo.h"

extern StringInfo ddl_gen_schema(Oid db_oid);
extern StringInfo ddl_gen_tables(Oid db_oid);
extern StringInfo ddl_gen_indexes(Oid db_oid);
extern StringInfo ddl_gen_types(Oid db_oid);
extern StringInfo ddl_gen_functions(Oid db_oid);
extern StringInfo ddl_gen_sequences(Oid db_oid);
extern StringInfo ddl_gen_views(Oid db_oid);
extern StringInfo ddl_gen_rules(Oid db_oid);
extern StringInfo ddl_gen_triggers(Oid db_oid);
extern StringInfo ddl_gen_row_security(Oid db_oid);
extern StringInfo ddl_gen_constraints(Oid db_oid);
extern StringInfo ddl_gen_all(Oid db_oid);

#endif
