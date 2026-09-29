using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// When the database cannot be reached, the API says so: a JSON 503 with errorCode
/// <c>SERVICE_UNAVAILABLE</c>, <c>retryAfterSeconds</c> and <c>Retry-After</c>, never the 500 that
/// read as a bug, and a Warning that names the SQL error numbers (ADR-0058).
/// </summary>
/// <remarks>
/// <para>
/// Each failure is made on a host that started against the real test database: the factory reads
/// its connection string each time it builds a request's context, so switching it after start makes
/// every later request meet the failure while the host itself came up normally.
/// </para>
/// <para>
/// Before this change each of these answered 500 through <c>GlobalExceptionHandler</c>, measured by
/// these tests, and the SQL error numbers were logged nowhere: the handler logged the outer
/// exception's message, and <c>SqlException.Number</c> is only the first of its errors anyway.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class DatabaseUnavailableSqlServerTests : IDisposable
{
    /// <summary>The outage <c>Retry-After</c>: EF's back-off cap, for machines (ADR-0058).</summary>
    internal const int OutageRetryAfterSeconds = 10;

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly ITestOutputHelper _output;
    private readonly List<CustomWebApplicationFactory> _factories = [];

    public DatabaseUnavailableSqlServerTests(ITestOutputHelper output) => _output = output;

    private CustomWebApplicationFactory StartedHost(Action<CustomWebApplicationFactory>? configure = null)
    {
        var factory = new CustomWebApplicationFactory();
        factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        factory.CaptureLog();
        configure?.Invoke(factory);
        _factories.Add(factory);
        _ = factory.CreateClient();
        return factory;
    }

    private static Task<HttpResponseMessage> SignInAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/auth/login", new { email = "outage@example.com", password = "TestPass123!" });

    [SqlServerFact]
    public async Task AMissingDatabase_AnswersTheOutage503_OnceTheRetriesAreSpent()
    {
        // The factory's own retry budget, 3 retries under a 5-second cap: 4060 is retried by EF.
        var factory = StartedHost(f => f.EnableSqlRetryOnFailure());
        var client = factory.CreateClient();

        factory.SetConnectionString(new SqlConnectionStringBuilder(SqlServerFactAttribute.ConnectionString!)
        {
            InitialCatalog = "AzureBankMissing_" + Guid.NewGuid().ToString("N"),
        }.ConnectionString);

        var clock = Stopwatch.StartNew();
        var response = await SignInAsync(client);
        clock.Stop();
        Print("missing database", response, clock, factory);

        await AssertServiceUnavailableAsync(response, applied: null);
        // The outage Warning itself: EF's own Error line for the failed query also prints the
        // exception, "Error Number:4060" included, so a line merely containing 4060 proves nothing.
        factory.CapturedLog.Should().Contain(
            line => line.StartsWith("[Warning] Service unavailable", StringComparison.Ordinal)
                    && line.Contains("4060", StringComparison.Ordinal),
            "the Warning names every SQL error number in the chain: 4060 is 'cannot open database'");
    }

    [SqlServerFact]
    public async Task ARefusedConnection_AnswersTheOutage503()
    {
        var refused = new SqlConnectionStringBuilder(SqlServerFactAttribute.ConnectionString!)
        {
            DataSource = "tcp:127.0.0.1,1",
            ConnectTimeout = 1,
        }.ConnectionString;

        // What SqlClient says for a refused connection on the OS this runs on, printed for the
        // record: the number differs between Windows and Linux.
        try
        {
            await using var connection = new SqlConnection(refused);
            await connection.OpenAsync();
            _output.WriteLine("unexpected: the refused address accepted a connection");
        }
        catch (SqlException e)
        {
            _output.WriteLine(
                $"refused connection on {Environment.OSVersion.Platform}: numbers "
                + string.Join(", ", e.Errors.Cast<SqlError>().Select(x => $"{x.Number} (class {x.Class})"))
                + $"; {e.Message}");
        }

        var factory = StartedHost();
        var client = factory.CreateClient();
        factory.SetConnectionString(refused);

        var clock = Stopwatch.StartNew();
        var response = await SignInAsync(client);
        clock.Stop();
        Print("refused connection", response, clock, factory);

        await AssertServiceUnavailableAsync(response, applied: null);
    }

    [SqlServerFact]
    public async Task AnExhaustedPool_AnswersTheOutage503()
    {
        // A pool of one, and a request holding it: the next request waits Connect Timeout for a
        // connection and gets SqlClient's pool-timeout InvalidOperationException.
        var pooled = new SqlConnectionStringBuilder(SqlServerFactAttribute.ConnectionString!)
        {
            MaxPoolSize = 1,
            ConnectTimeout = 3,
        }.ConnectionString;
        var factory = StartedHost(f => f.SetConnectionString(pooled));
        var client = factory.CreateClient();

        var hold = new HoldingReadInterceptor("[AspNetUsers]");
        factory.AddInterceptor(hold);
        var holding = SignInAsync(client);
        await hold.Held.WaitAsync(TimeSpan.FromSeconds(15));

        HttpResponseMessage response;
        var clock = Stopwatch.StartNew();
        try
        {
            response = await SignInAsync(client);
            clock.Stop();
        }
        finally
        {
            hold.Release();
            await holding;
        }

        Print("exhausted pool", response, clock, factory);
        await AssertServiceUnavailableAsync(response, applied: null);
    }

    [SqlServerFact]
    public async Task ALookupThatFailsTransiently_Answers503_WithoutSayingNothingWasApplied()
    {
        // The key's first request completed: the money moved. Its retry fails reading the key, so
        // this request never owned a claim and cannot know what was applied. "applied: false" here
        // would tell the visitor a deposit that happened did not.
        var factory = StartedHost();
        var client = factory.CreateClient();
        var (token, accountId) = await RegisterAsync(client);
        var key = Guid.NewGuid();

        (await DepositAsync(client, token, accountId, key)).StatusCode.Should().Be(HttpStatusCode.Created);

        // Through this host's non-retrying strategy the fault reaches the handlers as EF's
        // InvalidOperationException "likely due to a transient failure", with the TimeoutException
        // inside it (measured); with retries on, a fault that persists arrives as
        // RetryLimitExceededException. Either way the transient is found by walking the chain.
        var fault = new TransientFailureInterceptor("FROM [IdempotencyRecords]");
        factory.AddInterceptor(fault);
        var clock = Stopwatch.StartNew();
        var response = await DepositAsync(client, token, accountId, key);
        clock.Stop();
        Print("key lookup fault", response, clock, factory);

        fault.Fired.Should().BeTrue("the proof is void unless the key's lookup was actually faulted");
        await AssertServiceUnavailableAsync(response, applied: null);
    }

    /// <summary>
    /// The outage 503 (ADR-0058). <paramref name="applied"/> null asserts the key is ABSENT: only a
    /// money request that owns its claim and has started no commit can say what was applied.
    /// </summary>
    internal static async Task<JsonElement> AssertServiceUnavailableAsync(HttpResponseMessage response, bool? applied)
    {
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, $"the body was: {text}");
        response.Headers.RetryAfter.Should().NotBeNull("an outage 503 says when to come back");
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(OutageRetryAfterSeconds));
        response.Headers.CacheControl.Should().NotBeNull("an outage answer must never be cached");
        response.Headers.CacheControl!.NoStore.Should().BeTrue("an outage answer must never be cached");

        var body = JsonSerializer.Deserialize<JsonElement>(text);
        body.GetProperty("status").GetInt32().Should().Be(503);
        body.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.ServiceUnavailable);
        body.GetProperty("retryAfterSeconds").GetInt32().Should().Be(
            OutageRetryAfterSeconds, "the BFF does not always pass the header on: the body carries it too");
        body.TryGetProperty("traceId", out _).Should().BeTrue("it turns a screenshot into a log lookup");

        if (applied is { } expected)
        {
            body.TryGetProperty("applied", out var value).Should().BeTrue($"this answer knows what was applied: {text}");
            value.GetBoolean().Should().Be(expected);
        }
        else
        {
            body.TryGetProperty("applied", out _).Should().BeFalse(
                "this request cannot know what was applied, so it must not say");
        }

        return body;
    }

    private void Print(string what, HttpResponseMessage response, Stopwatch clock, CustomWebApplicationFactory factory)
    {
        _output.WriteLine($"{what}: {(int)response.StatusCode} after {clock.ElapsedMilliseconds} ms: "
                          + response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        foreach (var line in factory.CapturedLog)
        {
            _output.WriteLine("  log " + line);
        }
    }

    private static async Task<(string Token, Guid AccountId)> RegisterAsync(HttpClient client)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"dbu_{unique}",
            Email = $"dbu{unique}@example.com",
            Password = "TestPass123!",
            FirstName = "Data",
            LastName = "Base",
        }, Json);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        return (result!.Data!.Token.AccessToken, result.Data.Account.Id);
    }

    private static Task<HttpResponseMessage> DepositAsync(HttpClient client, string token, Guid accountId, Guid key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/deposit")
        {
            Content = JsonContent.Create(
                new DepositRequest { AccountId = accountId, Amount = 40m, Description = "Outage proof" },
                options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(IdempotencyConstants.HeaderName, key.ToString());
        return client.SendAsync(request);
    }

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }
}
