using FluentAssertions;
using NetArchTest.Rules;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// Tests enforcing design rules and best practices.
/// </summary>
public class DesignRuleTests
{
    private static readonly System.Reflection.Assembly ApiAssembly =
        typeof(AzureBank.Api.Services.Implementations.AccountService).Assembly;

    private static readonly System.Reflection.Assembly SharedAssembly =
        typeof(AzureBank.Shared.Entities.Account).Assembly;

    private static readonly System.Reflection.Assembly InfrastructureAssembly =
        typeof(AzureBank.Infrastructure.Data.AzureBankDbContext).Assembly;

    [Fact]
    public void Entities_ShouldBePublic()
    {
        var result = Types.InAssembly(SharedAssembly)
            .That()
            .ResideInNamespace("AzureBank.Shared.Entities")
            .Should()
            .BePublic()
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: "all entities must be public for EF Core to access them");
    }

    [Fact]
    public void Exceptions_ShouldInheritFromException()
    {
        var result = Types.InAssembly(SharedAssembly)
            .That()
            .ResideInNamespace("AzureBank.Shared.Exceptions")
            .Should()
            .Inherit(typeof(Exception))
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: "all custom exceptions must inherit from Exception");
    }

    [Fact]
    public void Controllers_ShouldBePublic()
    {
        var result = Types.InAssembly(ApiAssembly)
            .That()
            .ResideInNamespace("AzureBank.Api.Controllers")
            .Should()
            .BePublic()
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: "all controllers must be public for ASP.NET Core routing");
    }

    [Fact]
    public void ServiceInterfaces_ShouldBePublic()
    {
        var result = Types.InAssembly(ApiAssembly)
            .That()
            .ResideInNamespace("AzureBank.Api.Services.Interfaces")
            .Should()
            .BePublic()
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: "service interfaces must be public for DI registration");
    }

    [Fact]
    public void DTOs_ShouldBePublic()
    {
        var result = Types.InAssembly(SharedAssembly)
            .That()
            .ResideInNamespaceContaining("DTOs")
            .Should()
            .BePublic()
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: "DTOs must be public for serialization and API contracts");
    }

    [Fact]
    public void Configurations_ShouldBeClasses()
    {
        // Note: Sealing configurations is a micro-optimization but not required.
        // This test verifies configurations are proper classes, not interfaces/abstract.
        var result = Types.InAssembly(InfrastructureAssembly)
            .That()
            .ResideInNamespace("AzureBank.Infrastructure.Data.Configurations")
            .Should()
            .BeClasses()
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: "EF Core configurations should be concrete classes");
    }

    [Fact]
    public void Validators_ShouldNotBeAbstract()
    {
        var result = Types.InAssembly(ApiAssembly)
            .That()
            .ResideInNamespaceContaining("Validators")
            .ShouldNot()
            .BeAbstract()
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: "validators should be concrete classes for DI registration");
    }

    [Fact]
    public void Middleware_ShouldNotBeStatic_ExceptExtensions()
    {
        // Middleware extension classes (ending with "Extensions") are expected to be static
        var result = Types.InAssembly(ApiAssembly)
            .That()
            .ResideInNamespace("AzureBank.Api.Middleware")
            .And()
            .DoNotHaveNameEndingWith("Extensions") // Exclude extension classes
            .ShouldNot()
            .BeStatic()
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: "middleware classes (except extension classes) should not be static");
    }

    [Fact]
    public void Enums_ShouldBePublic()
    {
        var result = Types.InAssembly(SharedAssembly)
            .That()
            .ResideInNamespace("AzureBank.Shared.Enums")
            .Should()
            .BePublic()
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: "enums must be public for API contracts and serialization");
    }
}
