using ControlHome.TestSupport;

// One SQL Server (container or CONTROLHOME_TEST_SQLSERVER) for the whole assembly.
[assembly: AssemblyFixture(typeof(SqlServerFixture))]
