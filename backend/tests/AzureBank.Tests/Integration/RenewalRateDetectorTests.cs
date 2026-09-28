using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Serilog.Events;

namespace AzureBank.Tests.Integration;

/// <summary>
/// 06 §10 O2k through the real API host: four renewals of one grant within one access-token
/// lifetime all get 200 and raise one <c>RefreshRenewalRateHigh</c>; two raise nothing. The detector
/// is the host's own singleton, fed the <c>ReceivedAt</c> stamp of each request.
/// </summary>
/// <remarks>
/// A factory per test, because the log capture is set before the host starts. That the renewals
/// send no write command is proved on SQL Server, where commands exist:
/// <c>RefreshTokenRotationSqlServerTests.FourRenewalsThatRaiseTheRateEvent_SendNoWriteCommand…</c>.
/// </remarks>
public sealed class RenewalRateDetectorTests : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly CustomWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData(4, 1)]
    [InlineData(2, 0)]
    public async Task RenewalsOfOneGrant_AllGet200_AndOnlyMoreThanThreeRaiseTheEvent(int renewals, int events)
    {
        _factory.CaptureLog(LogEventLevel.Warning);
        var client = _factory.CreateClient();
        var (userId, grant) = await RegisterAsync(client);

        for (var i = 0; i < renewals; i++)
        {
            var renewal = await client.PostAsJsonAsync(
                "/api/auth/refresh", new RefreshRequest { RefreshToken = grant }, Json);
            renewal.StatusCode.Should().Be(HttpStatusCode.OK,
                $"the detector refuses nothing (renewal {i + 1}, body: {await renewal.Content.ReadAsStringAsync()})");
        }

        var raised = _factory.CapturedLog.Where(l => l.Contains(SecurityEvents.RefreshRenewalRateHigh)).ToList();
        raised.Should().HaveCount(events);
        if (events == 1)
        {
            raised[0].Should().StartWith("[Warning]").And.Contain(userId.ToString());
            raised[0].Should().NotContain(grant, "a grant is never logged (06 O2h)");
        }
    }

    private static async Task<(Guid UserId, string Grant)> RegisterAsync(HttpClient client)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"rate_{unique}",
            Email = $"rate{unique}@example.com",
            Password = "SecurePass123!",
            FirstName = "Rate",
            LastName = "Count"
        }, Json);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        return (body!.Data!.User.Id, body.Data.Token.RefreshToken!);
    }
}
