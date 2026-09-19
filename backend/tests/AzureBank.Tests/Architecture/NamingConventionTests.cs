using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
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

        allTypes.Should().NotBeEmpty(
            "a scan that finds no service passes while checking nothing; see ArchitectureRuleExtensions");

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
            .GetResult()
            .OverAtLeastOneType();

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
            .GetResult()
            .OverAtLeastOneType();

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
            .GetResult()
            .OverAtLeastOneType();

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
            .GetResult()
            .OverAtLeastOneType();

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
            .GetResult()
            .OverAtLeastOneType();

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
            .GetResult()
            .OverAtLeastOneType();

        result.IsSuccessful.Should().BeTrue(
            because: "all custom exceptions should end with 'Exception'");
    }

    [Fact]
    public void RequestDTOs_ShouldHaveNameEndingWithRequest()
    {
        /*
          A REQUEST DTO IS WHAT AN ACTION BINDS FROM THE BODY. Until 2026-09-11 this selected the DTO
          types whose names end with "Request" and asserted that their names end with "Request",
          which no type can fail: a renamed request DTO simply left the selection. Measured that
          day, the controllers bind 15 [FromBody] types and every one ends with "Request".
        */
        var bodies = ControllerActions()
            .SelectMany(action => action.GetParameters())
            .Where(parameter => parameter.GetCustomAttribute<FromBodyAttribute>() is not null)
            .Select(parameter => parameter.ParameterType)
            .Distinct()
            .ToList();

        bodies.Should().NotBeEmpty("the API binds request bodies, so an empty scan is a broken scan");
        bodies.Where(type => !type.Name.EndsWith("Request", StringComparison.Ordinal))
            .Select(type => type.Name)
            .Should().BeEmpty(because: "all request DTOs should end with 'Request'");
    }

    [Fact]
    public void ResponseDTOs_ShouldHaveNameEndingWithResponse()
    {
        /*
          A RESPONSE DTO IS WHAT AN ACTION DECLARES IT RETURNS, inside the ApiResponse<T> or
          PaginatedResponse<T> envelope, and the same tautology stood here until 2026-09-11. The
          envelopes are generic and are skipped; what they carry is unwrapped, List<T> included,
          and kept when it is one of this project's DTOs.
        */
        var declared = ControllerActions()
            .SelectMany(action => action.GetCustomAttributes<ProducesResponseTypeAttribute>())
            .SelectMany(attribute => WithTypeArguments(attribute.Type))
            .Where(type => !type.IsGenericType
                && type.Namespace?.StartsWith("AzureBank.Shared.DTOs", StringComparison.Ordinal) == true)
            .Distinct()
            .ToList();

        declared.Should().NotBeEmpty("the API declares its response bodies, so an empty scan is a broken scan");
        declared.Where(type => !type.Name.EndsWith("Response", StringComparison.Ordinal))
            .Select(type => type.Name)
            .Should().BeEmpty(because: "all response DTOs should end with 'Response'");
    }

    private static IEnumerable<MethodInfo> ControllerActions() =>
        ApiAssembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));

    private static IEnumerable<Type> WithTypeArguments(Type? type) =>
        type is null ? [] : [type, .. type.GetGenericArguments().SelectMany(WithTypeArguments)];
}
