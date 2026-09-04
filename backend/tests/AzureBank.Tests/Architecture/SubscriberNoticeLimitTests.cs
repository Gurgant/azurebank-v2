using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// Two tripwires for the limit ADR-0045 states: nothing in the API can send, and the withdrawn
/// citation does not come back.
/// </summary>
/// <remarks>
/// <para>
/// The first is the mechanical half of "the request records the obligation and never sends": a
/// mail dependency appearing in Api, Infrastructure or Shared is the one change that could quietly
/// move a send inside <c>POST /api/auth/pin</c>. The operator tool is EXCLUDED on purpose — it is
/// the one place a notice is rendered, and it hand-writes RFC 5322 rather than referencing a mail
/// library, which this test does not police.
/// </para>
/// <para>
/// WATCHED REFUSING before it shipped: with <c>_ = new System.Net.Mail.SmtpClient();</c> placed in
/// <c>AuthService.SetPinAsync</c>, the first test went red naming the type. A guard that has never
/// refused is a wish (<c>docs/engineering-traps.md</c>), which is why that sentence is here.
/// </para>
/// </remarks>
public class SubscriberNoticeLimitTests
{
    private static readonly System.Reflection.Assembly[] ApiSide =
    [
        typeof(AzureBank.Api.Services.Implementations.AuthService).Assembly,
        typeof(AzureBank.Infrastructure.Data.AzureBankDbContext).Assembly,
        typeof(AzureBank.Shared.Entities.SubscriberNotice).Assembly,
    ];

    [Fact]
    public void NothingOnTheApiSideCanSendMail_AndThisPinsTheLimit()
    {
        foreach (var assembly in ApiSide)
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOnAny("System.Net.Mail", "MailKit", "MimeKit", "SendGrid", "Twilio")
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                "{0} must not be able to send: the notice is RECORDED in the request and rendered later, "
                + "by the operator tool (ADR-0045) or by the API's own relay (ADR-0048) — both into a "
                + "pickup directory, neither through a mail library; a send inside the request would be "
                + "lost between the commit and the call or would hold the audit tail lock across I/O. "
                + "Offending types: {1}",
                assembly.GetName().Name,
                string.Join(", ", result.FailingTypes?.Select(t => t.FullName) ?? []));
        }
    }

    /*
      A PROSE GUARD, admitted as such. Five public sites cited "NIST SP 800-63-4B §4.1.2" — a
      document that does not exist under that name, and a quote cut before "as described in
      Sec. 4.6", which is where the requirements the ADR has to answer for actually live. The
      withdrawn wording is the kind that returns by copy-paste, so the repo's own rule — grep for
      the OLD wording when a claim is withdrawn — is made mechanical for this one string.
    */
    [Fact]
    public void TheWithdrawnCitation_DoesNotReturn()
    {
        var root = RepoRoot();
        string[] scopes = ["backend/src", "backend/tools", "docs/adr", "docs/deferred", "docs/architecture", "docs/runbooks"];

        var hits = scopes
            .Select(s => Path.Combine(root, s))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".md", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f)
                .Select((line, i) => (file: Path.GetRelativePath(root, f), line: i + 1, text: line))
                .Where(x => x.text.Contains("800-63-4B", StringComparison.Ordinal)))
            .Select(x => $"{x.file}:{x.line}")
            .ToList();

        hits.Should().BeEmpty(
            "the document is NIST SP 800-63B-4 (Revision 4 of volume B); '800-63-4B' was withdrawn "
            + "at five sites by ADR-0045 and must not be re-typed");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "backend", "AzureBank.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("this guard reads the sources; one that cannot find them must say so rather than pass");
        return dir!.FullName;
    }
}
