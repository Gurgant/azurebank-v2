using Microsoft.Extensions.Options;

namespace AzureBank.Bff.Options;

/// <summary>
/// Fails startup when <c>Spa:RootPath</c> is set but holds no <c>index.html</c> (ADR-0054).
/// </summary>
/// <remarks>
/// The static-file middleware would not complain: it serves what it finds, and a mistyped path finds
/// nothing, so every page would answer 404 from a host that reports itself healthy. The same rule as
/// the other startup checks here (ADR-0013): a misconfiguration stops the app instead of degrading it.
/// </remarks>
public sealed class SpaOptionsValidator(IHostEnvironment environment) : IValidateOptions<SpaOptions>
{
    public ValidateOptionsResult Validate(string? name, SpaOptions options)
    {
        var root = options.ResolveRoot(environment.ContentRootPath);
        if (root is null || File.Exists(Path.Combine(root, "index.html")))
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(
            $"Spa:RootPath is set but '{root}' holds no index.html. Build the SPA (npm run build) " +
            "or unset Spa:RootPath to serve no pages.");
    }
}
