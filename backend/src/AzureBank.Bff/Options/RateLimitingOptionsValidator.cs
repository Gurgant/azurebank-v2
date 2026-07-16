using Microsoft.Extensions.Options;

namespace AzureBank.Bff.Options;

/// <summary>
/// Fails startup on a rate-limiter misconfiguration (ADR-0013). The limiter builds its
/// window options lazily, inside the partition factory — so without this check a
/// non-positive limit or window would let the app start "healthy" and then throw on every
/// request that hits the limiter, i.e. a security control silently down. Fail fast instead.
/// </summary>
public sealed class RateLimitingOptionsValidator : IValidateOptions<RateLimitingOptions>
{
    public ValidateOptionsResult Validate(string? name, RateLimitingOptions options)
    {
        var errors = new List<string>();

        if (options.GlobalPermitLimit < 1)
        {
            errors.Add("RateLimiting:GlobalPermitLimit must be >= 1.");
        }
        if (options.GlobalWindowSeconds < 1)
        {
            errors.Add("RateLimiting:GlobalWindowSeconds must be >= 1.");
        }
        if (options.AuthPermitLimit < 1)
        {
            errors.Add("RateLimiting:AuthPermitLimit must be >= 1.");
        }
        if (options.AuthWindowSeconds < 1)
        {
            errors.Add("RateLimiting:AuthWindowSeconds must be >= 1.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
