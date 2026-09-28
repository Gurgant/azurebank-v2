using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.Entities;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;
using Xunit;

namespace AzureBank.Tests.Integration;

/// <summary>
/// ADR-0057 §10 O2h on the EF InMemory database: "a capture sink finds no grant in any log line or
/// audit row". The same walk on SQL Server, where a renewal in flight can be made as well, is
/// <see cref="GrantSweepSqlServerTests"/>.
/// </summary>
public sealed class GrantSweepTests
{
    [Fact]
    public Task NoGrantReachesALogLineOrAnAuditRow_AcrossEveryPathAGrantTakes() =>
        GrantSweep.ProveAsync(connectionString: null);
}

/// <summary>ADR-0057 §10 O2h on real SQL Server; see <see cref="GrantSweepTests"/>.</summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class GrantSweepSqlServerTests
{
    [SqlServerFact]
    public Task NoGrantReachesALogLineOrAnAuditRow_AcrossEveryPathAGrantTakes() =>
        GrantSweep.ProveAsync(SqlServerFactAttribute.ConnectionString);
}

/// <summary>
/// The walk itself: every path a grant takes through the API, with every line the API writes
/// captured down to Verbose, then every captured line and every audit row written meanwhile read
/// for any grant it handed out or was shown.
/// </summary>
/// <remarks>
/// The lines and rows the walk must produce are asserted first (the positive controls): an empty
/// sweep of a walk that wrote nothing would prove nothing. The grants are looked for whole and by
/// every run of twelve or more letters and digits in them, so a writer that escaped a <c>+</c> or a
/// <c>/</c> would not hide one.
/// </remarks>
internal static class GrantSweep
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private const string Password = "SecurePass123!";

    /// <summary>What appsettings.json raises to Warning, and the sweep lowers again.</summary>
    private static readonly string[] QuietNamespaces =
    [
        "Microsoft.AspNetCore",
        "Microsoft.AspNetCore.Hosting.Diagnostics",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.EntityFrameworkCore.Database.Command",
        "System",
    ];

    public static async Task ProveAsync(string? connectionString)
    {
        using var api = new CustomWebApplicationFactory();
        if (connectionString is not null)
        {
            api.SetConnectionString(connectionString);
        }
        api.CaptureLog(LogEventLevel.Verbose);
        using var host = api.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Serilog:MinimumLevel:Default", "Verbose");
            foreach (var quiet in QuietNamespaces)
            {
                builder.UseSetting($"Serilog:MinimumLevel:Override:{quiet}", "Verbose");
            }

            // The capture takes everything; the console, which a WriteTo in configuration replaces
            // (Program), keeps to what every other test host prints.
            builder.UseSetting("Serilog:WriteTo:0:Name", "Console");
            builder.UseSetting("Serilog:WriteTo:0:Args:restrictedToMinimumLevel", "Information");
        });
        var client = host.CreateClient();
        var firstRow = await LastAuditSequenceAsync(host) + 1;
        var grants = new List<string>();

        // Sign-in: a registration and a login, each issuing a grant.
        var (userId, email, registered) = await RegisterAsync(client);
        var (access, renewing) = await LoginAsync(client, email);
        grants.AddRange([registered, renewing]);

        // Renewal: four within one token lifetime, which also raises the rate event.
        for (var i = 0; i < 4; i++)
        {
            (await RefreshAsync(client, renewing)).Should().Be(HttpStatusCode.OK, $"renewal {i + 1}");
        }

        // Revoke, then the revoked grant presented again: the tripwire.
        (await client.PostAsJsonAsync("/api/auth/revoke", new RevokeRequest { RefreshTokens = [registered] }, Json))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await RefreshAsync(client, registered)).Should().Be(HttpStatusCode.Unauthorized, "the tripwire");

        // A grant the API never issued.
        var unknown = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        grants.Add(unknown);
        (await RefreshAsync(client, unknown)).Should().Be(HttpStatusCode.Unauthorized);

        // Off the token road: an address that is not loopback.
        (await RefreshAsync(client, renewing, from: "10.0.0.7")).Should().Be(HttpStatusCode.NotFound);

        // An expired grant.
        var (_, expiring) = await LoginAsync(client, email);
        grants.Add(expiring);
        await ExpireNewestGrantAsync(host, userId);
        (await RefreshAsync(client, expiring)).Should().Be(HttpStatusCode.Unauthorized, "the grant has expired");

        // A renewal in flight when its session ended: made on SQL Server only, where a read can be held.
        if (connectionString is not null)
        {
            var (_, inFlight) = await LoginAsync(client, email);
            grants.Add(inFlight);
            var hold = new HoldingReadInterceptor("[TokenHash] = ");
            api.AddInterceptor(hold);
            var renewal = RefreshAsync(client, inFlight);
            (await Task.WhenAny(hold.Held, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(hold.Held);
            (await client.PostAsJsonAsync("/api/auth/revoke", new RevokeRequest { RefreshTokens = [inFlight] }, Json))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            hold.Release();
            (await renewal).Should().Be(HttpStatusCode.Unauthorized, "revoked while the renewal was in flight");
        }

        // Sign out everywhere, then a grant it revoked presented again.
        using (var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout"))
        {
            logout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            (await client.SendAsync(logout)).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        (await RefreshAsync(client, renewing)).Should().Be(HttpStatusCode.Unauthorized, "signed out everywhere");

        var lines = api.CapturedEvents.ToArray();
        var rows = await AuditRowsFromAsync(host, firstRow);

        // The positive controls: the walk wrote what it walked through.
        using (new AssertionScope())
        {
            Rendered(lines).Count(l => l.Contains("Issued refresh token")).Should().BeGreaterThanOrEqualTo(3);
            Rendered(lines).Should().Contain(l => l.Contains(SecurityEvents.RefreshRenewalRateHigh));
            Rendered(lines).Should().Contain(l => l.Contains(SecurityEvents.RefreshTokenReuse));
            Rendered(lines).Should().Contain(l => l.Contains(SecurityEvents.RefreshTokenUnknown));
            Rendered(lines).Should().Contain(l => l.Contains("request to a token endpoint: \"not loopback\""));
            Rendered(lines).Should().Contain(l => l.Contains("is expired"));
            Rendered(lines).Should().Contain(l => l.Contains("(\"SignOutEverywhere\")"));
            lines.Should().Contain(e => e.Level == LogEventLevel.Verbose || e.Level == LogEventLevel.Debug,
                "the sink must have been given the lines below Information, or the sweep saw only part of them");
            rows.Select(r => r.Event).Should().Contain([SecurityEvents.RefreshTokenReuse, SecurityEvents.RefreshTokenUnknown]);
            if (connectionString is not null)
            {
                Rendered(lines).Should().Contain(l => l.Contains("while its renewal was in flight"));
            }
        }

        // The sweep.
        var needles = grants.SelectMany(Needles).Distinct().ToArray();
        var lineHits = lines
            .SelectMany(e => Texts(e).Select(text => (Line: e.MessageTemplate.Text, Text: text)))
            .Where(h => needles.Any(n => h.Text.Contains(n, StringComparison.Ordinal)))
            .Select(h => h.Line)
            .Distinct()
            .ToList();
        var rowHits = rows
            .Where(r => needles.Any(n => Columns(r).Contains(n, StringComparison.Ordinal)))
            .Select(r => r.Event)
            .ToList();
        using (new AssertionScope())
        {
            lineHits.Should().BeEmpty($"no grant in any of the {lines.Length} log lines captured (ADR-0057 §10 O2h)");
            rowHits.Should().BeEmpty($"no grant in any of the {rows.Count} audit rows written (ADR-0057 §10 O2h)");
        }
    }

    /// <summary>The whole grant, and every run of twelve or more letters and digits in it.</summary>
    private static IEnumerable<string> Needles(string grant) =>
        Regex.Matches(grant, "[A-Za-z0-9]{12,}").Select(m => m.Value).Prepend(grant);

    /// <summary>Everything a log line carries as text: its message, its template, every property, its exception.</summary>
    private static IEnumerable<string> Texts(LogEvent line) =>
        new[] { line.RenderMessage(CultureInfo.InvariantCulture), line.MessageTemplate.Text, line.Exception?.ToString() ?? "" }
            .Concat(line.Properties.Select(p => $"{p.Key}={p.Value}"));

    private static IEnumerable<string> Rendered(IEnumerable<LogEvent> lines) =>
        lines.Select(e => e.RenderMessage(CultureInfo.InvariantCulture));

    /// <summary>Every column of an audit row, as text.</summary>
    private static string Columns(AuditEvent row) =>
        string.Join("\n", typeof(AuditEvent).GetProperties().Select(p => $"{p.Name}={p.GetValue(row)}"));

    private static async Task<HttpStatusCode> RefreshAsync(HttpClient client, string grant, string? from = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh")
        {
            Content = JsonContent.Create(new RefreshRequest { RefreshToken = grant }, options: Json)
        };
        if (from is not null)
        {
            request.Headers.Add(FakeRemoteAddressStartupFilter.HeaderName, from);
        }

        return (await client.SendAsync(request)).StatusCode;
    }

    private static async Task<(Guid UserId, string Email, string Grant)> RegisterAsync(HttpClient client)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var email = $"sweep{unique}@example.com";
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"sweep_{unique}",
            Email = email,
            Password = Password,
            FirstName = "Grant",
            LastName = "Sweep"
        }, Json);
        response.EnsureSuccessStatusCode();
        var data = (await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json))!.Data!;
        return (data.User.Id, email, data.Token.RefreshToken!);
    }

    private static async Task<(string Access, string Grant)> LoginAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = email, Password = Password }, Json);
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(Json))!.Data!.Token;
        return (token.AccessToken, token.RefreshToken!);
    }

    /// <summary>Backdates the user's newest grant: the option refuses a lifetime short enough to wait out.</summary>
    private static async Task ExpireNewestGrantAsync(WebApplicationFactory<Program> host, Guid userId)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var newest = await db.RefreshTokens.Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt).FirstAsync();
        newest.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    private static async Task<long> LastAuditSequenceAsync(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.AuditEvents.AsNoTracking().MaxAsync(e => (long?)e.Sequence) ?? 0;
    }

    private static async Task<List<AuditEvent>> AuditRowsFromAsync(WebApplicationFactory<Program> host, long firstSequence)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.AuditEvents.AsNoTracking().Where(e => e.Sequence >= firstSequence).ToListAsync();
    }
}
