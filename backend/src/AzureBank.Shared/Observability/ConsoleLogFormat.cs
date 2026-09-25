namespace AzureBank.Shared.Observability;

/// <summary>
/// What a host writes to its console: one JSON object per line in Production, text everywhere
/// else. Shared by the API and the BFF, as <see cref="OtlpEndpointGuard"/> is, so the two
/// containers cannot drift apart; takes primitives, so Shared gains no hosting dependency.
/// </summary>
/// <remarks>
/// JSON because a container's output is collected line by line. Measured on the two containers
/// running as Production (2026-09-25), before this: every line was text, and a refusal at startup
/// printed its exception as a stack trace over many lines, each one a record of its own to whatever
/// collects them. Text stays for the development loop, where a person reads it, and for every other
/// name, Staging included: JSON is for the one environment this repository runs its images in
/// (compose.yaml), unlike HSTS and the __Host- cookie, which cover everything but Development. An
/// in-process test host's bootstrap lines follow the process's variables, so they are JSON when
/// none is set; its host lines follow the environment the test gives it.
/// </remarks>
public static class ConsoleLogFormat
{
    /// <summary>For the host's own logger, which knows its environment: JSON in Production.</summary>
    public static bool IsJson(string environmentName) =>
        string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// For the bootstrap logger, built before the host exists, from the variables the host is about
    /// to read: <c>DOTNET_ENVIRONMENT</c> first, then <c>ASPNETCORE_ENVIRONMENT</c>, and Production
    /// when neither is set, which is the host's own default. That order is measured, not assumed:
    /// with <c>DOTNET_ENVIRONMENT=Development</c> and <c>ASPNETCORE_ENVIRONMENT=Testing</c> the API
    /// ran as Development (2026-09-11, recorded in FailedStartupExitCodeTests), and on 2026-09-25,
    /// Production against Development both ways round, <c>DOTNET_ENVIRONMENT</c> decided each time
    /// and the bootstrap line came out in the host's format. An <c>--environment</c> argument is
    /// not read here.
    /// </summary>
    public static bool IsJsonBeforeTheHost(Func<string, string?> variable)
    {
        ArgumentNullException.ThrowIfNull(variable);
        return IsJson(Named(variable, "DOTNET_ENVIRONMENT") ?? Named(variable, "ASPNETCORE_ENVIRONMENT") ?? "Production");
    }

    private static string? Named(Func<string, string?> variable, string name) =>
        variable(name) is { Length: > 0 } value ? value : null;
}
