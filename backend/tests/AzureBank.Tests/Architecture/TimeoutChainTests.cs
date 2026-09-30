extern alias bff;

using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Xunit;
using BackendApiOptions = bff::AzureBank.Bff.Options.BackendApiOptions;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// The waits nest, innermost first: the API gives up on a request before the BFF gives up on the
/// API, and the BFF before the ingress in front of it (ADR-0058).
/// </summary>
/// <remarks>
/// <para>
/// An answer the API gives before any commit can say <c>applied: false</c>: nothing was changed,
/// and the same key is safe to send again. That answer is only useful if it reaches the visitor.
/// If the BFF stopped waiting first it would answer its own 503, which cannot say what the API
/// did, and a certain "nothing was changed" would reach the visitor as "unknown".
/// </para>
/// <para>
/// The API's last answer before a commit can come as late as its deadline, plus up to 5 s for SQL
/// Server to acknowledge the cancelled command (the attention), plus the 3-second release of the
/// idempotency claim, plus up to 5 s more if that release is itself cancelled. With a 40-second
/// deadline that is 53 s, under the BFF's 55. With a 45-second deadline it would be 58.
/// </para>
/// <para>
/// Read from the settings each host ships, and from the code default that applies when a setting is
/// absent.
/// </para>
/// </remarks>
public class TimeoutChainTests
{
    /// <summary>How long SQL Server may take to acknowledge a cancelled command.</summary>
    private const int AttentionSeconds = 5;

    /// <summary>The budget of the idempotency claim's release on the error path, read from the middleware.</summary>
    private static double ReleaseSeconds => AzureBank.Api.Middleware.IdempotencyMiddleware.ReleaseBudget.TotalSeconds;

    /// <summary>Azure Container Apps' ingress request timeout, which nothing here can change.</summary>
    private const int IngressSeconds = 240;

    /// <summary>The BFF's renewal wait, which a proxied request can spend before it is forwarded.</summary>
    private const int RenewalWaitSeconds = 5;

    [Fact]
    public void EveryPreCommitAnswerBeatsTheBff()
    {
        var deadline = ApiRequestDeadlineSeconds();
        var bff = BffTimeoutSeconds();

        (deadline + AttentionSeconds + ReleaseSeconds + AttentionSeconds).Should().BeLessThan(
            bff,
            "the API's last answer before a commit ({0} s deadline + {1} s attention + {2} s release + "
            + "{1} s attention) must reach the visitor before the BFF's own {3} s timeout replaces it",
            deadline, AttentionSeconds, ReleaseSeconds, bff);

        (RenewalWaitSeconds + bff).Should().BeLessThan(
            IngressSeconds, "the BFF answers before the ingress cuts the connection");
    }

    /// <summary>
    /// <c>RequestDeadline:Seconds</c> as the API ships it, and the code default behind it.
    /// </summary>
    private static int ApiRequestDeadlineSeconds()
    {
        using var settings = Settings("AzureBank.Api");
        settings.RootElement.TryGetProperty("RequestDeadline", out var section).Should().BeTrue(
            "the API's appsettings.json names its request deadline, where an operator looks for it");
        var shipped = section.GetProperty("Seconds").GetInt32();

        var options = typeof(AzureBank.Shared.Options.DatabaseOptions).Assembly
            .GetType("AzureBank.Shared.Options.RequestDeadlineOptions");
        options.Should().NotBeNull("the deadline's code default lives in RequestDeadlineOptions");
        var codeDefault = (int)options!.GetProperty("Seconds")!.GetValue(Activator.CreateInstance(options))!;
        codeDefault.Should().Be(shipped, "the code default and the shipped setting are one number");

        return shipped;
    }

    /// <summary><c>BackendApi:TimeoutSeconds</c> as the BFF ships it, and the code default behind it.</summary>
    private static int BffTimeoutSeconds()
    {
        using var settings = Settings("AzureBank.Bff");
        var shipped = settings.RootElement.GetProperty("BackendApi").GetProperty("TimeoutSeconds").GetInt32();

        new BackendApiOptions().TimeoutSeconds.Should().Be(
            shipped, "the code default and the shipped setting are one number");

        return shipped;
    }

    private static JsonDocument Settings(string project)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull(because: "the test reads the settings each host ships");
        var path = Path.Combine(dir!.FullName, "backend", "src", project, "appsettings.json");
        return JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
    }
}
