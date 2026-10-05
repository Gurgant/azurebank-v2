using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Serilog.Events;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Real Kestrel proofs: routing must set the monetary body limit before authentication, and MVC
/// must not try to set it again after idempotency has read the body. TestServer has no size feature.
/// </summary>
public sealed class KestrelRequestSizeLimitTests : IDisposable
{
    private const string FilterSource = "Microsoft.AspNetCore.Mvc.Filters.RequestSizeLimitFilter";
    private const string RoutingSource = "Microsoft.AspNetCore.Routing.EndpointRoutingMiddleware";
    private const string AuthenticationSource = "Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler";
    private const string HandlerSource = "AzureBank.Api.Handlers.AppExceptionHandler";
    private const string DepositPath = "/api/transactions/deposit";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly DebugLogFactory _factory = new();
    private readonly HttpClient _client;
    private readonly ITestOutputHelper _output;

    public KestrelRequestSizeLimitTests(ITestOutputHelper output)
    {
        _output = output;
        _factory.CaptureLog(LogEventLevel.Debug);
        _factory.UseKestrel(0);
        _factory.StartServer();
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task MoneyRequests_DoNotReapplyTheLimitAfterIdempotencyReadsTheBody()
    {
        var (token, accountId) = await RegisterAsync();
        var requests = new[]
        {
            (DepositPath, HttpStatusCode.Created, (string?)null),
            ("/api/transactions/withdraw", HttpStatusCode.Unauthorized, ErrorCodes.AuthorizationRequired),
            ("/api/transfers", HttpStatusCode.Unauthorized, ErrorCodes.AuthorizationRequired),
            ("/api/transfers/internal", HttpStatusCode.NotFound, ErrorCodes.AccountNotFound),
        };

        foreach (var (path, status, errorCode) in requests)
        {
            using var response = await SendAsync(path, MoneyBody(path, accountId), token);
            response.StatusCode.Should().Be(status);
            if (errorCode is not null)
            {
                await AssertErrorCodeAsync(response, errorCode);
            }
        }

        // Positive control in this very host: absence of the MVC event only counts if the sink
        // is listening. The oversized refusal must still be logged at Warning by the handler.
        using var oversized = await SendAsync(DepositPath, OversizedBody(accountId), token);
        oversized.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        await AssertErrorCodeAsync(oversized, ErrorCodes.IdempotencyPayloadTooLarge);
        _factory.CapturedEvents.Should().Contain(e =>
            IsSource(e, HandlerSource)
            && e.Level == LogEventLevel.Warning
            && e.Properties["ErrorCode"].Equals(new ScalarValue(ErrorCodes.IdempotencyPayloadTooLarge)));

        var filterEvents = _factory.CapturedEvents.Where(e => IsSource(e, FilterSource)).ToList();
        foreach (var e in filterEvents)
        {
            _output.WriteLine($"{e.Level} {e.Properties["EventId"]}: {e.RenderMessage()}");
        }

        var messages = filterEvents.Select(e => $"{e.Level}: {e.RenderMessage()}").ToList();
        messages.Should().BeEmpty(
            "routing must apply the limit before idempotency reads the body, without MVC applying it again");
    }

    // Controls: both lengths are refused identically before and after the metadata change.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Control_OversizedDeposit_Is413WithTheSameCode(bool chunked)
    {
        var (token, accountId) = await RegisterAsync();
        using var response = await SendAsync(DepositPath, OversizedBody(accountId), token, chunked);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        await AssertErrorCodeAsync(response, ErrorCodes.IdempotencyPayloadTooLarge);
    }

    [Fact]
    public async Task Control_InLimitChunkedDeposit_Is201()
    {
        var (token, accountId) = await RegisterAsync();
        using var response = await SendAsync(DepositPath, MoneyBody(DepositPath, accountId), token, chunked: true);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // Controls: the four mints do not read through idempotency, so their MVC filter can still set
    // the feature. Its Debug event also proves that this category has not been silenced.
    [Theory]
    [InlineData("/api/transactions/withdraw/authorizations", HttpStatusCode.BadRequest)]
    [InlineData("/api/transfers/authorizations", HttpStatusCode.BadRequest)]
    [InlineData("/api/transfers/internal/authorizations", HttpStatusCode.BadRequest)]
    [InlineData("/api/accounts/{id}/deletion-authorizations", HttpStatusCode.UnprocessableEntity)]
    public async Task Control_MintFilter_SetsTheLimitWithoutAWarning(string path, HttpStatusCode status)
    {
        var (token, accountId) = await RegisterAsync();
        path = path.Replace("{id}", accountId.ToString(), StringComparison.Ordinal);
        using var response = await SendAsync(path, """{"pin":"123456"}""", token, idempotent: false);

        response.StatusCode.Should().Be(status);
        var events = _factory.CapturedEvents.Where(e => IsSource(e, FilterSource)).ToList();
        events.Should().NotContain(e => e.Level >= LogEventLevel.Warning);
        var applied = events.Where(e => HasEvent(e, 3, "MaxRequestBodySizeSet")).Should().ContainSingle().Subject;
        applied.Level.Should().Be(LogEventLevel.Debug);
        applied.Properties["RequestSize"].Should().Be(new ScalarValue("32768"));
    }

    // Controls: removing endpoint metadata or setting the limit only inside idempotency would
    // fail these. Without a token the request never reaches idempotency, but routing still caps it.
    [Theory]
    [InlineData("/api/transactions/deposit")]
    [InlineData("/api/transactions/withdraw")]
    [InlineData("/api/transfers")]
    [InlineData("/api/transfers/internal")]
    public async Task Control_UnauthenticatedMoneyRequest_Sets32768OnceBeforeAuthentication(string path)
    {
        using var response = await SendAsync(path, MoneyBody(path, Guid.NewGuid()), token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var events = _factory.CapturedEvents.ToList();
        var applied = events.Where(e => IsSource(e, RoutingSource) && HasEvent(e, 11, "MaxRequestBodySizeSet"))
            .Should().ContainSingle().Subject;
        applied.Level.Should().Be(LogEventLevel.Debug);
        applied.Properties["RequestSize"].Should().Be(new ScalarValue("32768"));

        var authenticationIndex = events.FindIndex(e => IsSource(e, AuthenticationSource));
        authenticationIndex.Should().BeGreaterThanOrEqualTo(0, "the authentication log must be captured too");
        events.IndexOf(applied).Should().BeLessThan(authenticationIndex);
    }

    private async Task<(string Token, Guid AccountId)> RegisterAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        using var response = await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"size_{unique}",
            Email = $"size{unique}@example.com",
            Password = "TestPass123!",
            FirstName = "Body",
            LastName = "Limit",
        }, Json);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        result.Should().NotBeNull();
        (result!.Data).Should().NotBeNull();
        return (result.Data!.Token.AccessToken, result.Data.Account.Id);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string path, string body, string? token, bool chunked = false, bool idempotent = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = chunked ? new ChunkedJsonContent(body) : new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (chunked)
        {
            request.Content.Headers.ContentLength.Should().BeNull();
            request.Headers.TransferEncodingChunked = true;
        }
        else
        {
            request.Content.Headers.ContentLength.Should().Be(Encoding.UTF8.GetByteCount(body));
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (idempotent)
        {
            request.Headers.Add(IdempotencyConstants.HeaderName, Guid.NewGuid().ToString());
        }
        return await _client.SendAsync(request);
    }

    private static string MoneyBody(string path, Guid accountId)
    {
        object body = path switch
        {
            DepositPath => new { accountId, amount = 250m, description = "Body limit proof" },
            "/api/transactions/withdraw" => new { accountId, amount = 1m },
            "/api/transfers" => new { fromAccountId = accountId, recipientAzureTag = "missing_payee", amount = 1m },
            "/api/transfers/internal" => new { fromAccountId = accountId, toAccountId = Guid.NewGuid(), amount = 1m },
            _ => throw new ArgumentOutOfRangeException(nameof(path)),
        };
        return JsonSerializer.Serialize(body, Json);
    }

    private static string OversizedBody(Guid accountId) => MoneyBody(DepositPath, accountId).PadRight(40_000);

    private static async Task AssertErrorCodeAsync(HttpResponseMessage response, string errorCode)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errorCode").GetString().Should().Be(errorCode);
    }

    private static bool IsSource(LogEvent e, string source) =>
        e.Properties.TryGetValue("SourceContext", out var value) && value.Equals(new ScalarValue(source));

    private static bool HasEvent(LogEvent e, int id, string name) =>
        e.Properties.TryGetValue("EventId", out var value)
        && value is StructureValue structure
        && structure.Properties.Any(p => p.Name == "Id" && p.Value.Equals(new ScalarValue(id)))
        && structure.Properties.Any(p => p.Name == "Name" && p.Value.Equals(new ScalarValue(name)));

    private sealed class DebugLogFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Serilog:MinimumLevel:Override:Microsoft.AspNetCore", "Debug");
        }
    }

    private sealed class ChunkedJsonContent : HttpContent
    {
        private readonly byte[] _body;

        public ChunkedJsonContent(string body)
        {
            _body = Encoding.UTF8.GetBytes(body);
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_body, 0, _body.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
