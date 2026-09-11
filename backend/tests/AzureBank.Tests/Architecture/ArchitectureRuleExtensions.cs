using FluentAssertions;
using NetArchTest.Rules;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// A NetArchTest rule reports success on an EMPTY selection, so every rule here first asserts that
/// it selected something.
/// </summary>
/// <remarks>
/// Measured 2026-09-11: with <c>AzureBank.Shared.Entitiez</c> and <c>AzureBank.Api.Mapperz</c> in
/// place of the real namespaces, all 18 tests in <see cref="DesignRuleTests"/> and
/// <see cref="NamingConventionTests"/> passed. That is the shape a renamed or moved namespace leaves
/// behind: green, and checking nothing.
/// </remarks>
internal static class ArchitectureRuleExtensions
{
    /// <summary>Fails unless the rule was about at least one type; returns the result unchanged.</summary>
    public static TestResult OverAtLeastOneType(this TestResult result)
    {
        result.SelectedTypesForTesting.Should().NotBeEmpty(
            "a rule that selects no type passes while checking nothing, which is exactly what a "
            + "renamed, moved or misspelt namespace looks like");
        return result;
    }
}
