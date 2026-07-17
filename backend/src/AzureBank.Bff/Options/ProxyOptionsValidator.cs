using System.Net;
using Microsoft.Extensions.Options;

namespace AzureBank.Bff.Options;

/// <summary>
/// Fails startup on an unusable reverse-proxy trust configuration (ADR-0013). A typo in
/// KnownProxies would otherwise be skipped silently, leaving X-Forwarded-For untrusted and
/// collapsing every client into a single rate-limit partition — the security control is
/// then quietly down with nothing but a log line to notice it. A warning is not enough for
/// a control whose failure mode is invisible; refuse to start instead.
/// </summary>
public sealed class ProxyOptionsValidator : IValidateOptions<ProxyOptions>
{
    public ValidateOptionsResult Validate(string? name, ProxyOptions options)
    {
        if (options.KnownProxies is null)
        {
            return ValidateOptionsResult.Fail("ForwardedHeaders:KnownProxies must not be null.");
        }

        var errors = new List<string>();

        foreach (var proxy in options.KnownProxies)
        {
            if (!IPAddress.TryParse(proxy, out _))
            {
                errors.Add($"ForwardedHeaders:KnownProxies contains '{proxy}', which is not a valid IP address.");
            }
        }

        if (options.KnownProxies.Length > 0 && options.ForwardLimit < 1)
        {
            errors.Add("ForwardedHeaders:ForwardLimit must be >= 1 when KnownProxies is configured.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
