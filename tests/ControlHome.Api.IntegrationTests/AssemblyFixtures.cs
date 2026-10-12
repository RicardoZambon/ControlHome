using ControlHome.TestSupport;

// One SQL Server (container or CONTROLHOME_TEST_SQLSERVER) for the whole assembly; each API factory gets its own database.
[assembly: AssemblyFixture(typeof(SqlServerFixture))]
