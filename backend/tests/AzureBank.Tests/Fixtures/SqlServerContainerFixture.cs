using Testcontainers.MsSql;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// xUnit collection fixture that manages a shared SQL Server container.
/// Container is started once per test collection, not per test.
///
/// Usage:
///   [Collection("SqlServer")]
///   public class MyIntegrationTests : IntegrationTestBase
///   {
///       public MyIntegrationTests(SqlServerContainerFixture dbFixture, CustomWebApplicationFactory factory)
///           : base(factory)
///       {
///           factory.SetConnectionString(dbFixture.ConnectionString);
///       }
///   }
/// </summary>
public class SqlServerContainerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container;

    public SqlServerContainerFixture()
    {
        _container = new MsSqlBuilder()
            .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
            .WithPassword("YourStrong@Passw0rd!")
            .Build();
    }

    /// <summary>
    /// Gets the connection string for the running SQL Server container.
    /// </summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <summary>
    /// Called by xUnit before any tests in the collection run.
    /// Starts the SQL Server container.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _container.StartAsync();
    }

    /// <summary>
    /// Called by xUnit after all tests in the collection complete.
    /// Stops and disposes the SQL Server container.
    /// </summary>
    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}

/// <summary>
/// xUnit collection definition for SQL Server integration tests.
/// Tests marked with [Collection("SqlServer")] will share the same container.
/// </summary>
[CollectionDefinition("SqlServer")]
public class SqlServerCollection : ICollectionFixture<SqlServerContainerFixture>
{
    // This class has no code, and is never created.
    // Its purpose is to be the place to apply [CollectionDefinition] and
    // all the ICollectionFixture<> interfaces.
}
