using AzureBank.Api.HealthChecks;
using AzureBank.Infrastructure.Data;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Readiness must fail for a login that can READ the audit store but not WRITE to it.
/// </summary>
/// <remarks>
/// <para>
/// Reading was never the capability that matters. ADR-0044 D1 refuses a money movement when its
/// audit row cannot be WRITTEN, so a principal holding SELECT without INSERT breaks every deposit,
/// withdrawal and transfer while a read-only probe reports Healthy — an instance kept in rotation
/// that cannot move a penny. That is the exact blind spot readiness exists to close.
/// </para>
/// <para>
/// SQL-gated because permissions are a SQL Server concept; the InMemory provider has none, and the
/// check short-circuits to "not applicable" there. <c>EXECUTE AS USER</c> rather than a second login
/// so the test needs no mixed-mode authentication: it switches the security context of the very
/// connection the health check is about to use.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class AuditWritePermissionSqlServerTests : IDisposable
{
    private const string ReadOnlyUser = "audit_readonly_probe";

    private readonly ITestOutputHelper _output;
    private CustomWebApplicationFactory? _factory;

    public AuditWritePermissionSqlServerTests(ITestOutputHelper output) => _output = output;

    [SqlServerFact]
    public async Task APrincipalThatCanReadButNotInsert_IsReportedUnhealthy()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _ = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var check = new AuditChainHealthCheck(context);

        await context.Database.OpenConnectionAsync();

        try
        {
            await ExecAsync(context, $"""
                IF DATABASE_PRINCIPAL_ID('{ReadOnlyUser}') IS NOT NULL DROP USER [{ReadOnlyUser}];
                CREATE USER [{ReadOnlyUser}] WITHOUT LOGIN;
                GRANT SELECT ON dbo.AuditEvents TO [{ReadOnlyUser}];
                DENY INSERT ON dbo.AuditEvents TO [{ReadOnlyUser}];
                """);

            // THE CONTROL, taken FIRST: as the test's own principal the check is healthy. Without
            // this, an Unhealthy caused by anything else at all would satisfy the assertion below.
            var asOwner = await check.CheckHealthAsync(new HealthCheckContext());
            asOwner.Status.Should().Be(
                HealthStatus.Healthy,
                "the same store, the same connection — only the security context changes below");

            await ExecAsync(context, $"EXECUTE AS USER = '{ReadOnlyUser}';");

            var asReader = await check.CheckHealthAsync(new HealthCheckContext());

            await ExecAsync(context, "REVERT;");

            _output.WriteLine($"read-only principal -> {asReader.Status}: {asReader.Description}");

            asReader.Status.Should().Be(
                HealthStatus.Unhealthy,
                "SELECT without INSERT means every money movement is refused by D1, so this instance "
                + "must be taken out of rotation even though nothing about the store is broken");
            asReader.Description.Should().Contain(
                "NOT writable",
                "the operator has to be able to tell a permission problem from an outage — the fixes "
                + "have nothing in common");

            // And back again, proving the Unhealthy tracked the permission rather than a one-way
            // door the first call happened to close.
            var afterRevert = await check.CheckHealthAsync(new HealthCheckContext());
            afterRevert.Status.Should().Be(
                HealthStatus.Healthy,
                "reverting the security context restores INSERT, so the verdict must follow it back");
        }
        finally
        {
            await ExecAsync(context, $"""
                IF DATABASE_PRINCIPAL_ID('{ReadOnlyUser}') IS NOT NULL DROP USER [{ReadOnlyUser}];
                """);
            await context.Database.CloseConnectionAsync();
        }
    }

    private static Task ExecAsync(AzureBankDbContext context, string sql) =>
        context.Database.ExecuteSqlRawAsync(sql);

    public void Dispose() => _factory?.Dispose();
}
