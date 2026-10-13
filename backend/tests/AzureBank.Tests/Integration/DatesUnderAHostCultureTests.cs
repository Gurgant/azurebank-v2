using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// A real response carries its dates as RFC 3339 text in UTC when the request runs under a culture
/// that writes dates its own way.
/// </summary>
/// <remarks>
/// The culture is the request's, not the process's: a startup filter ahead of the pipeline sets
/// <see cref="CultureInfo.CurrentCulture"/> on the request's flow, as request localisation would,
/// so no other test sees it.
/// </remarks>
public class DatesUnderAHostCultureTests : IntegrationTestBase
{
    private const string Rfc3339 = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    public DatesUnderAHostCultureTests(CustomWebApplicationFactory factory) : base(factory) { }

    [Theory]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    [InlineData("fi-FI")]
    public async Task Register_UnderACultureWithItsOwnDates_AnswersItsDatesInRfc3339(string culture)
    {
        using var host = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter>(new RequestCultureStartupFilter(culture))));
        using var client = host.CreateClient();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];

        var before = DateTime.UtcNow;
        using var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"test_user_{uniqueId}",
            Email = $"test{uniqueId}@example.com",
            Password = TestUserPassword,
            FirstName = "Test",
            LastName = "User"
        }, JsonOptions);
        var body = await response.Content.ReadAsStringAsync();

        response.IsSuccessStatusCode.Should().BeTrue(body);
        using var document = JsonDocument.Parse(body);
        var data = document.RootElement.GetProperty("data");
        var dates = new[]
        {
            data.GetProperty("token").GetProperty("expiresAt").GetString(),
            data.GetProperty("account").GetProperty("createdAt").GetString(),
        };
        foreach (var date in dates)
        {
            DateTime.TryParseExact(
                    date, Rfc3339, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var instant)
                .Should().BeTrue($"'{date}' is not RFC 3339 text in UTC");
            instant.Should().BeCloseTo(before, TimeSpan.FromDays(1), $"'{date}' is not an instant near now");
        }
    }

    private sealed class RequestCultureStartupFilter(string culture) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                return nextMiddleware(context);
            });

            next(app);
        };
    }
}
