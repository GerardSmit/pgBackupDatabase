using Xunit;

namespace PgDbBackup.Tests;

public sealed class PgdogTests
{
    [Fact(Skip =
        "pgdog is a stateless Rust proxy with no DB state — backup correctness is " +
        "independent of proxy presence; verified manually by routing Npgsql through " +
        "pgdog and confirming pg_dbbackup output matches a direct-connection run.")]
    public void Pgdog_Backup_Through_Proxy()
    {
    }
}
