using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Tests.Fixtures;
using FluentAssertions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// THE concurrency proof of ADR-0009, reusable against any host (EF InMemory
/// smoke and real SQL Server): N byte-identical parallel requests with the
/// same Idempotency-Key (the burst) must produce EXACTLY ONE execution, and
/// the same request sent once more, after the whole burst has been answered,
/// must be answered as a replay: the winner's status, the Idempotency-Replayed
/// header with the value true, and a response text exactly equal to the
/// winner's (the text a body decodes to: no bytes are compared).
///
/// Every helper is parallel-safe: authorization travels per request message,
/// never via HttpClient.DefaultRequestHeaders.
/// </summary>
internal static class IdempotencyConcurrencyProof
{
    /// <summary>The PIN this proof enrols and then spends at the mint endpoint (ADR-0042).</summary>
    private const string TestPin = "123456";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>
    /// N identical parallel transfers, one key → exactly one execution:
    /// - exactly ONE 201 without Idempotency-Replayed
    /// - every other response is a replay (201 + header) whose text is exactly
    ///   the winner's, or a 409 IDEMPOTENCY_IN_FLIGHT; they can all be 409s
    /// - the same request sent once more after the burst is a replay: the
    ///   winner's status, the header, and exactly the winner's text
    /// - balances move exactly once (mathematically verified)
    /// - exactly one TransferOut / TransferIn transaction pair exists
    /// </summary>
    public static async Task RunTransferProofAsync(
        HttpClient client, int parallelism, Action<string> log)
    {
        var sender = await RegisterUserAsync(client);
        var recipient = await RegisterUserAsync(client);

        const decimal funding = 1000m;
        const decimal amount = 50m;
        await DepositAsync(client, sender, funding);
        await SetPinAsync(client, sender);

        var body = JsonSerializer.Serialize(new TransferRequest
        {
            FromAccountId = sender.AccountId,
            RecipientAzureTag = recipient.AzureTag,
            Amount = amount,
            Description = "Concurrency proof"
        }, Json);

        /*
          ONE authorisation, presented by all N parallel requests. It is spendable exactly once and
          the idempotency claim is winnable exactly once, so this proof now exercises both
          single-use mechanisms at the same time — and either failing would show up as more than one
          debit, which is what the balance assertions below already measure.
        */
        var authorization = await AuthoriseTransferAsync(
            client, sender, recipient.AzureTag, amount);

        var outcome = await FireIdenticalRequestsAsync(
            client, sender.Token, "/api/transfers", body, parallelism, log, authorization);

        // Exactly one execution, mathematically:
        (await GetBalanceAsync(client, sender)).Should().Be(
            funding - amount, "the sender must be debited exactly once");
        (await GetBalanceAsync(client, recipient)).Should().Be(
            amount, "the recipient must be credited exactly once");
        (await CountTransactionsAsync(client, sender)).Should().Be(
            2, "funding deposit + exactly ONE outgoing transfer");
        (await CountTransactionsAsync(client, recipient)).Should().Be(
            1, "exactly ONE incoming transfer");

        log($"transfer proof OK: 1 winner / {outcome.Replays} replays / {outcome.Conflicts} in-flight 409s over {parallelism} requests");
    }

    /// <summary>
    /// Same proof for deposits: N identical parallel deposits, one key →
    /// the balance grows by the amount exactly once, and the same request
    /// sent once more after the burst is a replay of the winner's answer.
    /// </summary>
    public static async Task RunDepositProofAsync(
        HttpClient client, int parallelism, Action<string> log)
    {
        var user = await RegisterUserAsync(client);
        const decimal amount = 200m;

        var body = JsonSerializer.Serialize(new DepositRequest
        {
            AccountId = user.AccountId,
            Amount = amount,
            Description = "Concurrency proof"
        }, Json);

        var outcome = await FireIdenticalRequestsAsync(
            client, user.Token, "/api/transactions/deposit", body, parallelism, log);

        (await GetBalanceAsync(client, user)).Should().Be(
            amount, "the deposit must execute exactly once");
        (await CountTransactionsAsync(client, user)).Should().Be(1);

        log($"deposit proof OK: 1 winner / {outcome.Replays} replays / {outcome.Conflicts} in-flight 409s over {parallelism} requests");
    }

    private sealed record ProofOutcome(int Replays, int Conflicts);

    /// <summary>
    /// Mints the authorisation the transfer proof presents. Bound to exactly the movement the raw
    /// body describes: a mismatch anywhere would be refused 401 and the proof would report zero
    /// winners instead of the one it is testing for.
    /// </summary>
    private static async Task<Guid> AuthoriseTransferAsync(
        HttpClient client, ProofUser sender, string recipientAzureTag, decimal amount)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/transfers/authorizations")
        {
            Content = JsonContent.Create(new TransferAuthorizationRequest
            {
                FromAccountId = sender.AccountId,
                RecipientAzureTag = recipientAzureTag,
                Amount = amount,
                Pin = TestPin
            }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sender.Token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content
            .ReadFromJsonAsync<ApiResponse<StepUpAuthorizationResponse>>(Json);
        return body!.Data!.AuthorizationId;
    }

    private static async Task<ProofOutcome> FireIdenticalRequestsAsync(
        HttpClient client, string token, string url, string rawBody, int parallelism, Action<string> log,
        Guid? stepUpAuthorizationId = null)
    {
        var key = Guid.NewGuid();
        log($"firing {parallelism} identical parallel POST {url} with key {key}...");

        async Task<(HttpStatusCode Status, bool Replayed, string? ReplayedValue, string Body)> SendOnceAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(rawBody, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add(IdempotencyConstants.HeaderName, key.ToString());
            if (stepUpAuthorizationId is { } authorizationId)
            {
                request.Headers.Add(StepUpConstants.HeaderName, authorizationId.ToString());
            }
            var response = await client.SendAsync(request);
            var replayed = response.Headers.TryGetValues(IdempotencyConstants.ReplayedHeaderName, out var values)
                ? string.Join(",", values)
                : null;
            return (Status: response.StatusCode,
                    Replayed: replayed is not null,
                    ReplayedValue: replayed,
                    Body: await response.Content.ReadAsStringAsync());
        }

        var responses = await Task.WhenAll(
            Enumerable.Range(0, parallelism).Select(_ => SendOnceAsync()));

        var winners = responses.Where(r => r.Status == HttpStatusCode.Created && !r.Replayed).ToList();
        var replays = responses.Where(r => r.Status == HttpStatusCode.Created && r.Replayed).ToList();
        var conflicts = responses.Where(r => r.Status == HttpStatusCode.Conflict).ToList();
        var anomalies = responses
            .Where(r => r.Status is not (HttpStatusCode.Created or HttpStatusCode.Conflict))
            .ToList();

        log($"results: winners={winners.Count} replays={replays.Count} conflicts={conflicts.Count} anomalies={anomalies.Count}");

        anomalies.Should().BeEmpty(
            $"only 201 (execute/replay) and 409 (in flight) are legal; got: " +
            string.Join(" | ", anomalies.Select(a => $"{(int)a.Status}: {Truncate(a.Body)}")));

        winners.Should().HaveCount(1,
            "the composite-PK claim allows EXACTLY ONE execution per (user, endpoint, key)");

        foreach (var replay in replays)
        {
            ComparableText.Of(replay.Body).Should().Be(ComparableText.Of(winners[0].Body),
                "replays are byte-identical to the original response");
        }

        foreach (var conflict in conflicts)
        {
            conflict.Body.Should().Contain(ErrorCodes.IdempotencyInFlight);
        }

        /*
          Inside the burst a replay is a matter of timing: a request that finds the key still being
          processed is answered 409, and every one but the winner can be. So the same request goes
          once more here, with every answer of the burst in hand: the API stores the winner's
          answer before sending it, and must answer this request with the same. Started before the
          burst has been awaited, it can be one more 409. It is a 409 too when the API could not
          store the winner's answer, which it sends all the same. The first assertion below fails
          with one message for both; the API's log tells them apart, with an Error line for the
          second: "Failed to store idempotency response".
        */
        var again = await SendOnceAsync();
        again.Status.Should().Be(winners[0].Status,
            "the identical request sent after the burst must replay the winner's status");
        again.ReplayedValue.Should().Be("true",
            "the identical request sent after the burst must carry the replay header, and the header must say true");
        ComparableText.Of(again.Body).Should().Be(ComparableText.Of(winners[0].Body),
            "the identical request sent after the burst must replay the winner's exact response text");

        return new ProofOutcome(replays.Count, conflicts.Count);
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200];

    private sealed record ProofUser(string Token, Guid AccountId, string AzureTag);

    private static async Task<ProofUser> RegisterUserAsync(HttpClient client)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var azureTag = $"proof_{uniqueId}";

        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = azureTag,
            Email = $"proof{uniqueId}@example.com",
            Password = "TestPass123!",
            FirstName = "Proof",
            LastName = "User"
        }, Json);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        return new ProofUser(result!.Data!.Token.AccessToken, result.Data.Account.Id, azureTag);
    }

    private static async Task SetPinAsync(HttpClient client, ProofUser user)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/pin")
        {
            Content = JsonContent.Create(new SetPinRequest { Pin = TestPin, Password = "TestPass123!" }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static async Task DepositAsync(HttpClient client, ProofUser user, decimal amount)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/deposit")
        {
            Content = JsonContent.Create(new DepositRequest
            {
                AccountId = user.AccountId,
                Amount = amount,
                Description = "Proof funding"
            }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        request.Headers.Add(IdempotencyConstants.HeaderName, Guid.NewGuid().ToString());
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<decimal> GetBalanceAsync(HttpClient client, ProofUser user)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/accounts/{user.AccountId}/balance");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<BalanceResponse>>(Json);
        return result!.Data!.Balance;
    }

    private static async Task<int> CountTransactionsAsync(HttpClient client, ProofUser user)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/transactions?accountId={user.AccountId}&pageSize=50");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var result = await response.Content
            .ReadFromJsonAsync<PaginatedResponse<TransactionResponse>>(Json);
        return result!.Pagination.TotalItems;
    }
}
