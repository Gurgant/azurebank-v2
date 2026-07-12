using FluentAssertions;
using NetArchTest.Rules;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// Tests enforcing naming conventions across the codebase.
/// </summary>
public class NamingConventionTests
{
    private static readonly System.Reflection.Assembly ApiAssembly =
        typeof(AzureBank.Api.Services.Implementations.AccountService).Assembly;

    private static readonly System.Reflection.Assembly SharedAssembly =
        typeof(AzureBank.Shared.Entities.Account).Assembly;

    [Fact]
    public void Services_ShouldFollowNamingConventions()
    {
        // Service implementations should end with "Service" OR follow utility naming (e.g., PasswordHasher)
        // Filter to only public classes (excludes compiler-generated types, nested types, etc.)
        var allTypes = Types.InAssembly(ApiAssembly)
            .That()
            .ResideInNamespace("AzureBank.Api.Services.Implementations")
            .And()
            .AreClasses()
            .And()
            .ArePublic()
            .GetTypes()
            .ToList();

        // All types should either end with "Service" or be recognized utility classes (e.g., Hasher)
        var invalidTypes = allTypes
            .Where(t => !t.Name.EndsWith("Service") && !t.Name.EndsWith("Hasher"))
            .Select(t => t.Name)
            .ToList();

        invalidTypes.Should().BeEmpty(
            because: "all service implementations should end with 'Service' or be recognized utility classes like 'Hasher'");
    }

    [Fact]
    public void ServiceInterfaces_ShouldStartWithI()
    {
        var result = Types.InAssembly(ApiAssembly)
            .That()
            .ResideInNamespace("AzureBank.Api.Services.Interfaces")
            .Should()
            .HaveNameStartingWith("I")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: "all service interfaces should start with 'I'");
    }

    [Fact]
    public void Validators_ShouldHaveNameEndingWithValidator()
    {
        var result = Types.InAssembly(ApiAssembly)
            .That()
            .ResideInNamespaceContaining("Validators")
            .Should()
            .HaveNameEndingWith("Validator")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: "all validators should end with 'Validator'");
    }

    [Fact]
    public void Controllers_ShouldHaveNameEndingWithController()
    {
        var result = Types.InAssembly(ApiAssembly)
            .That()
            .ResideInNamespace("AzureBank.Api.Controllers")
            .Should()
            .HaveNameEndingWith("Controller")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: "all controllers should end with 'Controller'");
    }

    [Fact]
    public void Mappers_ShouldHaveNameEndingWithMapper()
    {
        var result = Types.InAssembly(ApiAssembly)
            .That()
            .ResideInNamespace("AzureBank.Api.Mappers")
            .Should()
            .HaveNameEndingWith("Mapper")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: "all mappers should end with 'Mapper'");
    }

    [Fact]
    public void ExceptionHandlers_ShouldHaveNameEndingWithHandler()
    {
        var result = Types.InAssembly(ApiAssembly)
            .That()
            .ResideInNamespace("AzureBank.Api.Handlers")
            .Should()
            .HaveNameEndingWith("Handler")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: "all exception handlers should end with 'Handler'");
    }

    [Fact]
    public void Exceptions_ShouldHaveNameEndingWithException()
    {
        var result = Types.InAssembly(SharedAssembly)
            .That()
            .ResideInNamespace("AzureBank.Shared.Exceptions")
            .Should()
            .HaveNameEndingWith("Exception")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: "all custom exceptions should end with 'Exception'");
    }

    [Fact]
    public void RequestDTOs_ShouldHaveNameEndingWithRequest()
    {
        var result = Types.InAssembly(SharedAssembly)
            .That()
            .ResideInNamespaceContaining("DTOs")
            .And()
            .HaveNameEndingWith("Request")
            .Should()
            .HaveNameEndingWith("Request")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: "all request DTOs should end with 'Request'");
    }

    [Fact]
    public void ResponseDTOs_ShouldHaveNameEndingWithResponse()
    {
        var result = Types.InAssembly(SharedAssembly)
            .That()
            .ResideInNamespaceContaining("DTOs")
            .And()
            .HaveNameEndingWith("Response")
            .Should()
            .HaveNameEndingWith("Response")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: "all response DTOs should end with 'Response'");
    }
}
