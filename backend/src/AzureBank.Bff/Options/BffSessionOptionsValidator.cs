using Microsoft.Extensions.Options;

namespace AzureBank.Bff.Options;

/// <summary>
/// Fails startup on a session misconfiguration (ADR-0018). The cookie name feeds every
/// session read (controller, middleware, YARP transform, rate-limit partitioner) and the
/// timeouts ARE the session-lifetime control — a null/empty name or non-positive timeout
/// must stop the app, not surface as per-request failures with the control silently down.
/// </summary>
public sealed class BffSessionOptionsValidator : IValidateOptions<BffSessionOptions>
{
    public ValidateOptionsResult Validate(string? name, BffSessionOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.CookieName))
        {
            errors.Add("Session:CookieName must be a non-empty cookie name.");
        }
        if (options.InactivityTimeoutMinutes < 1)
        {
            errors.Add("Session:InactivityTimeoutMinutes must be >= 1.");
        }
        if (options.AbsoluteTimeoutMinutes < 1)
        {
            errors.Add("Session:AbsoluteTimeoutMinutes must be >= 1.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
