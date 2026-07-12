using FluentAssertions;
using NetArchTest.Rules;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing Clean Architecture layer dependencies.
///
/// Layer Structure:
///   Api (outer) → Infrastructure → Shared (inner)
///
/// Rules:
///   - Inner layers cannot reference outer layers
///   - Shared (domain) has no dependencies on other projects
/// </summary>
public class LayerDependencyTests
{
    private static readonly System.Reflection.Assembly ApiAssembly =
        typeof(AzureBank.Api.Services.Implementations.AccountService).Assembly;

    private static readonly System.Reflection.Assembly SharedAssembly =
        typeof(AzureBank.Shared.Entities.Account).Assembly;

    private static readonly System.Reflection.Assembly InfrastructureAssembly =
        typeof(AzureBank.Infrastructure.Data.AzureBankDbContext).Assembly;

    [Fact]
    public void SharedLayer_ShouldNotDependOn_ApiLayer()
    {
        var result = Types.InAssembly(SharedAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("AzureBank.Api")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: "Shared (domain) layer must not reference Api layer");
    }

    [Fact]
    public void SharedLayer_ShouldNotDependOn_InfrastructureLayer()
    {
        var result = Types.InAssembly(SharedAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("AzureBank.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: "Shared (domain) layer must not reference Infrastructure layer");
    }

    [Fact]
    public void InfrastructureLayer_ShouldNotDependOn_ApiLayer()
    {
        var result = Types.InAssembly(InfrastructureAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("AzureBank.Api")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: "Infrastructure layer must not reference Api layer");
    }

    [Fact]
    public void SharedLayer_ShouldNotDependOn_BffLayer()
    {
        var result = Types.InAssembly(SharedAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("AzureBank.Bff")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: "Shared (domain) layer must not reference BFF layer");
    }

    [Fact]
    public void InfrastructureLayer_ShouldNotDependOn_BffLayer()
    {
        var result = Types.InAssembly(InfrastructureAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("AzureBank.Bff")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: "Infrastructure layer must not reference BFF layer");
    }

    [Fact]
    public void ApiLayer_ShouldNotDependOn_BffLayer()
    {
        var result = Types.InAssembly(ApiAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("AzureBank.Bff")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: "Api layer must not reference BFF layer");
    }
}
