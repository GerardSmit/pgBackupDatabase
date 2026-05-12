using PgDbBackup.Tests.Fixtures;
using Xunit;

[assembly: AssemblyFixture(typeof(PgContainerFixture))]
[assembly: AssemblyFixture(typeof(PgWithExtensionsFixture))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]
