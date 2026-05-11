#ifndef SUBPROCESS_RECOVERY_H
#define SUBPROCESS_RECOVERY_H

#include "postgres.h"
#include "access/xlogdefs.h"
#include "utils/timestamp.h"

#include "bakfile.h"

typedef struct SubprocessRecoveryInput
{
	char	   *bak_filepath;
	const char *password;
	int			file_count;
	char	  **bak_filepaths;
	bool		has_stop_at;
	TimestampTz stop_at;
} SubprocessRecoveryInput;

typedef struct SubprocessRecoveryResult
{
	char	   *dump_filepath;
	char	   *tmp_pgdata;
	bool		ok;
	char	   *error_detail;
} SubprocessRecoveryResult;

extern bool subprocess_recovery_available(char **detail_out);

extern SubprocessRecoveryResult *subprocess_recover_and_dump(
	SubprocessRecoveryInput *input);

extern void subprocess_recovery_cleanup(SubprocessRecoveryResult *result);

extern bool subprocess_pg_restore(const char *dump_filepath,
								  const char *target_dbname,
								  char **error_detail_out);

#endif /* SUBPROCESS_RECOVERY_H */
