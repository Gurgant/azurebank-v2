using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Tests.Fixtures;
using FluentAssertions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// A page whose offset does not fit an int, on the provider that refuses a negative OFFSET.
/// </summary>
/// <remarks>
/// <para>
/// <c>(Page - 1) * PageSize</c> was int arithmetic, and <c>[Range]</c> lets Page reach
/// <c>int.MaxValue</c>. Measured 2026-09-11 against SQL Server, before the fix:
/// </para>
/// <code>
/// Page=2147483647&amp;PageSize=100 -> 500 "The offset specified in a OFFSET clause may not be negative."
/// Page=1073741825&amp;PageSize=20  -> 200 with the first page's rows, labelled page 1073741825
/// </code>
/// <para>
/// The InMemory provider cannot show the first: it skips a negative count as zero. That is the
/// only reason this lives behind <c>[SqlServerTheory]</c>; <c>TransactionEndpointTests</c> covers
/// the second on every run.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class PageOffsetSqlServerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private CustomWebApplicationFactory? _factory;

    [SqlServerTheory]
    [InlineData(2147483647, 100)]
    [InlineData(1073741825, 20)]
    public async Task APageWhoseOffsetOverflowsAnInt_IsAnEmptyPage(int page, int pageSize)
    {
        var (client, accountId) = await SeedAccountAsync();
        await DepositAsync(client, accountId, 100m);

        // The control: the user HAS a row, so an empty answer below is the paging and not the data.
        var first = await client.GetFromJsonAsync<PaginatedResponse<TransactionResponse>>(
            $"/api/transactions?Page=1&PageSize={pageSize}", Json);
        first!.Data.Should().NotBeEmpty();

        var response = await client.GetAsync($"/api/transactions?Page={page}&PageSize={pageSize}");

        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "a page past the end is an empty page, whatever its number");
        var result = await response.Content.ReadFromJsonAsync<PaginatedResponse<TransactionResponse>>(Json);
        result!.Data.Should().BeEmpty($"page {page} at {pageSize} a page starts far past every row");
        result.Pagination.Page.Should().Be(page);
        result.Pagination.TotalItems.Should().Be(first.Pagination.TotalItems);
    }

    private async Task<(HttpClient Client, Guid AccountId)> SeedAccountAsync()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        var client = _factory.CreateClient();

        var unique = Guid.NewGuid().ToString("N")[..8];
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"page_{unique}",
            Email = $"page{unique}@example.com",
            Password = "SecurePass123!",
            FirstName = "Page",
            LastName = "Offset",
        }, Json);
        response.EnsureSuccessStatusCode();

        var registered = (await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json))!.Data!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", registered.Token.AccessToken);

        return (client, registered.Account.Id);
    }

    private static async Task DepositAsync(HttpClient client, Guid accountId, decimal amount)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/deposit")
        {
            Content = JsonContent.Create(
                new DepositRequest { AccountId = accountId, Amount = amount }, options: Json),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await client.SendAsync(request)).EnsureSuccessStatusCode();
    }

    public void Dispose() => _factory?.Dispose();
}
