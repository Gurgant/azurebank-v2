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
        /*
          RETRY OFF, AND THIS IS THE LINE THAT MAKES THE WALK STREAM.

          EF does not let a retrying execution strategy coexist with streaming: a stream cannot be
          replayed from the middle, so QueryCompilationContext.IsBuffering is set from
          ExecutionStrategy.RetriesOnFailure and every query the context compiles PRE-BUFFERS its
          whole resultset. AsAsyncEnumerable() then streams in name only.

          Measured on this repo, 40,006 audit rows, identical table, only the composition differing:
          retry off 3 MB of managed heap, retry on 34 MB — and the 34 MB is already there before the
          first row is looked at. Left on, this tool would carry the entire audit table in memory,
          which is the exact cost the streaming change was made to remove.

          Losing the retry costs little here and nothing that matters: this is a READ-ONLY walk an
          operator runs deliberately, so a transient fault means running it again, while the
          alternative is an out-of-memory failure on the table it exists to read.
        */
        services.AddInfrastructure(configuration, environment, retryOnTransientFailures: false);

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
                "Audit:ChainKey must be configured with at least 32 characters. Set the "
                + "environment variable Audit__ChainKey. User-secrets are read only when "
                + "DOTNET_ENVIRONMENT=Development, and this tool defaults to Production. Without "
                + "the key it would report an intact chain as broken.")
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Scoped, matching the API, so the chain reads through the same DbContext the verifier owns.
        services.AddScoped<IAuditChain, AuditChain>();

        return services;
    }
}
