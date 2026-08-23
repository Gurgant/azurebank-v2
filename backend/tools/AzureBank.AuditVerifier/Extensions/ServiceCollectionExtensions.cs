using AzureBank.Infrastructure.Data;
using AzureBank.Infrastructure.Extensions;
using AzureBank.Shared.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AzureBank.AuditVerifier.Extensions;

/// <summary>
/// Registers the minimum needed to walk the chain: the DbContext, and the chain itself.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVerifierServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddInfrastructure(configuration, environment);

        /*
          THE SAME VALIDATION THE API APPLIES, and for a sharper reason here.

          The row hash is an HMAC keyed with Audit:ChainKey. A verifier holding the WRONG key does
          not fail — it recomputes every hash incorrectly and reports the chain broken at sequence 1.
          That is the worst possible outcome for a tamper-evidence tool: it accuses an intact chain,
          during exactly the incident where somebody is deciding whether the bank was attacked.

          So a missing or short key must stop this tool before it reads a single row, and say so.
          Mirrored from AzureBank.Api's registration deliberately — if the two ever disagree, the
          verifier is validating something the API does not, or worse, the reverse.
        */
        services.AddOptions<AuditOptions>()
            .Bind(configuration.GetSection(AuditOptions.SectionName))
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.ChainKey) && o.ChainKey.Length >= 32,
                "Audit:ChainKey must be configured with at least 32 characters "
                + "(dotnet user-secrets in development; see README). Without the key this tool "
                + "would report an intact chain as broken.")
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Scoped, matching the API, so the chain reads through the same DbContext the verifier owns.
        services.AddScoped<IAuditChain, AuditChain>();

        return services;
    }
}
