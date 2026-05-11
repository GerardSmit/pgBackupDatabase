#ifndef INSPECT_H
#define INSPECT_H

#include "postgres.h"
#include "fmgr.h"

extern Datum inspect_header(FunctionCallInfo fcinfo);
extern Datum inspect_filelist(FunctionCallInfo fcinfo);
extern Datum inspect_verify(FunctionCallInfo fcinfo);

#endif
