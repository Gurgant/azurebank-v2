using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Services.Implementations;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// How many passwords a guesser can test against one account in a window, on SQL Server:
/// <see cref="ValidationRules.MaxLoginAttempts"/>, also when the requests are sent together, at
/// sign-in and at the password proof of a first PIN, and a locked account's answer does not depend
/// on the password (ADR-0012 decisions 2 and 3).
/// </summary>
/// <remarks>
/// Behind <c>[SqlServerFact]</c> because requests that reach the user's row together exist only
/// there. The comparisons are counted in the account's password hasher, and only those made for a
/// user who has an email: the dummy verification the equaliser spends (ADR-0012 decision 6) is made
/// for a user who has none, and is no guess at anybody's password. Measured before the attempt was
/// counted ahead of its comparison: twenty passwords sent together were all compared, the right one
/// among them signed in, and a locked account answered the right password 429 and a wrong one 401.
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class LoginGuessBoundSqlServerTests : IDisposable
{
    private const string Password = "SecurePass123!";
    private const int Burst = 20;
    private const int Rounds = 5;

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly ITestOutputHelper _output;
    private readonly ComparisonCounter _counter = new();
    private CustomWebApplicationFactory? _factory;
    private WebApplicationFactory<Program>? _host;

    public LoginGuessBoundSqlServerTests(ITestOutputHelper output) => _output = output;

    [SqlServerFact]
    public async Task ABurstOfWrongPasswords_ComparesExactlyTheThreshold()
    {
        var client = CreateClient();

        // Every round is run and written out before any of them fails the test.
        using (new AssertionScope())
        {
            for (var round = 0; round < Rounds; round++)
            {
                var email = await RegisterAsync(client);
                _counter.Reset();

                var answers = await SendTogetherAsync(email, WrongPasswords(Burst));

                _output.WriteLine(
                    $"round {round}: sent {Burst} wrong passwords together, compared {_counter.Compared} with "
                    + $"the account's hash, answers {Tally(answers)}");

                // Exactly, not at most: a burst that compared none would pass "at most" too.
                _counter.Compared.Should().Be(
                    ValidationRules.MaxLoginAttempts,
                    $"round {round}: the window hands out that many comparisons, and only a counted attempt is compared");
                answers.Should().OnlyContain(
                    a => a == HttpStatusCode.Unauthorized,
                    $"round {round}: a wrong password and a request against the lock receive the same 401");
            }
        }
    }

    [SqlServerFact]
    public async Task ABurstHoldingTheRightPassword_SignsInOnlyWhenTheRightPasswordWasCompared()
    {
        var client = CreateClient();

        using (new AssertionScope())
        {
            for (var round = 0; round < Rounds; round++)
            {
                var email = await RegisterAsync(client);
                _counter.Reset();

                var answers = await SendTogetherAsync(email, [.. WrongPasswords(Burst - 1), Password]);
                var rightAnswer = answers[^1];

                _output.WriteLine(
                    $"round {round}: sent {Burst} together, the right one last; compared {_counter.Compared}; "
                    + $"the right one was answered {(int)rightAnswer}, compared as number {_counter.MatchedAt}; "
                    + $"all answers {Tally(answers)}");

                rightAnswer.Should().BeOneOf(
                    [HttpStatusCode.OK, HttpStatusCode.Unauthorized],
                    $"round {round}: the right password signs in or meets the lock's 401, never an answer of its own");
                (rightAnswer == HttpStatusCode.OK).Should().Be(
                    _counter.Matched == 1,
                    $"round {round}: the right password signs in when, and only when, it was compared");
                if (_counter.Matched == 1)
                {
                    // A sign-in clears the count, as ADR-0012 decision 2 says, so the requests of the
                    // burst that are counted after it start a window of their own: up to twice the
                    // threshold in all. What the bound holds is that the right password was one of
                    // the first MaxLoginAttempts compared.
                    _counter.MatchedAt.Should().BeInRange(
                        1, ValidationRules.MaxLoginAttempts,
                        $"round {round}: the right password signed in, so it was among the first comparisons");
                    _counter.Compared.Should().BeLessThanOrEqualTo(
                        2 * ValidationRules.MaxLoginAttempts, $"round {round}: one window before the sign-in, one after");
                }
                else
                {
                    _counter.Compared.Should().BeLessThanOrEqualTo(
                        ValidationRules.MaxLoginAttempts,
                        $"round {round}: with no sign-in, the burst is bounded as one of wrong passwords is");
                }
            }
        }
    }

    [SqlServerFact]
    public async Task OnceLocked_EveryPasswordIsAnsweredAsAnUnknownEmailIs_AndNoneIsCompared()
    {
        var client = CreateClient();
        var email = await RegisterAsync(client);

        for (var i = 0; i < ValidationRules.MaxLoginAttempts; i++)
        {
            (await LoginAsync(client, email, $"Wrong{i}Pass!")).Status.Should().Be(HttpStatusCode.Unauthorized);
        }

        _counter.Reset();
        var right = await LoginAsync(client, email, Password);
        var wrong = await LoginAsync(client, email, "WrongAgain9!");
        // An email nobody has spends the equaliser's dummy verification, which is no comparison.
        var unknown = await LoginAsync(client, $"nobody{Guid.NewGuid():N}@example.com", Password);
        _output.WriteLine($"locked, the right password: {(int)right.Status} {right.Body}\n{right.Headers}");
        _output.WriteLine($"locked, a wrong password: {(int)wrong.Status} {wrong.Body}\n{wrong.Headers}");
        _output.WriteLine($"an email nobody has: {(int)unknown.Status} {unknown.Body}\n{unknown.Headers}");

        right.Status.Should().Be(HttpStatusCode.Unauthorized);
        wrong.Status.Should().Be(HttpStatusCode.Unauthorized);
        unknown.Status.Should().Be(HttpStatusCode.Unauthorized);
        Comparable(right.Body).Should().Be(
            Comparable(wrong.Body),
            "once the threshold is spent, the answer must not tell a guesser which password is right");
        Comparable(right.Body).Should().Be(
            Comparable(unknown.Body), "nor may it tell that the account exists, or that it is locked");
        right.Headers.Should().Be(wrong.Headers, "no header may say it either");
        right.Headers.Should().Be(unknown.Headers, "no header may say it either");
        _counter.Compared.Should().Be(0, "a locked account's password is not read");
    }

    [SqlServerFact]
    public async Task AtTheFirstPinsPasswordProof_ABurstComparesExactlyTheThreshold_AndTheLockReadsNone()
    {
        var client = CreateClient();

        using (new AssertionScope())
        {
            for (var round = 0; round < Rounds; round++)
            {
                var token = await RegisterForTokenAsync(client);
                _counter.Reset();

                var answers = await SendPinEnrolmentsTogetherAsync(token, WrongPasswords(Burst));
                var compared = _counter.Compared;

                _counter.Reset();
                using var rightWhileLocked = await EnrolPinAsync(token, Password);
                var comparedWhileLocked = _counter.Compared;

                _output.WriteLine(
                    $"round {round}: sent {Burst} wrong passwords together, compared {compared}, answers "
                    + $"{Tally(answers)}; then the right one: {(int)rightWhileLocked.StatusCode}, "
                    + $"compared {comparedWhileLocked}");

                compared.Should().Be(
                    ValidationRules.MaxLoginAttempts, $"round {round}: the proof counts on the login's own limit");
                answers.Count(a => a == HttpStatusCode.Unauthorized).Should().Be(
                    ValidationRules.MaxLoginAttempts, $"round {round}: each password compared was wrong, and is a 401");
                answers.Count(a => a == HttpStatusCode.TooManyRequests).Should().Be(
                    Burst - ValidationRules.MaxLoginAttempts, $"round {round}: every other one met the lock's 429");
                rightWhileLocked.StatusCode.Should().Be(
                    HttpStatusCode.TooManyRequests, $"round {round}: the lock answers the right password too");
                comparedWhileLocked.Should().Be(0, $"round {round}: the lock refuses without reading the password");
            }
        }
    }

    private sealed record Answer(HttpStatusCode Status, string Body, string Headers);

    private sealed class ComparisonCounter
    {
        private int _compared;
        private int _matched;
        private int _matchedAt;

        /// <summary>Comparisons made against an account's own hash.</summary>
        public int Compared => Volatile.Read(ref _compared);

        /// <summary>How many of those found the password right.</summary>
        public int Matched => Volatile.Read(ref _matched);

        /// <summary>Which comparison, counted from 1 as they began, first found the password right; 0 when none did.</summary>
        public int MatchedAt => Volatile.Read(ref _matchedAt);

        public void Reset()
        {
            Volatile.Write(ref _compared, 0);
            Volatile.Write(ref _matched, 0);
            Volatile.Write(ref _matchedAt, 0);
        }

        /// <summary>Numbers a comparison as it begins; 0 for the equaliser's, which is no guess.</summary>
        public int Begin(ApplicationUser user) =>
            string.IsNullOrEmpty(user.Email) ? 0 : Interlocked.Increment(ref _compared);

        public void End(int number, PasswordVerificationResult result)
        {
            if (number != 0 && result != PasswordVerificationResult.Failed)
            {
                Interlocked.Increment(ref _matched);
                Interlocked.CompareExchange(ref _matchedAt, number, 0);
            }
        }
    }

    private sealed class CountingHasher(IPasswordHasher<ApplicationUser> inner, ComparisonCounter counter)
        : IPasswordHasher<ApplicationUser>
    {
        public string HashPassword(ApplicationUser user, string password) => inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(
            ApplicationUser user, string hashedPassword, string providedPassword)
        {
            var number = counter.Begin(user);
            var result = inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
            counter.End(number, result);
            return result;
        }
    }

    private HttpClient CreateClient()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            foreach (var existing in services.Where(d => d.ServiceType == typeof(IPasswordHasher<ApplicationUser>)).ToList())
            {
                services.Remove(existing);
            }
            services.AddScoped<IPasswordHasher<ApplicationUser>>(
                _ => new CountingHasher(new AccountPasswordHasher(), _counter));
        }));
        return _host.CreateClient();
    }

    /// <summary>One request per password, all released by one signal, each on a client of its own.</summary>
    private async Task<HttpStatusCode[]> SendTogetherAsync(string email, string[] passwords)
    {
        var clients = CreateClients(passwords.Length);
        try
        {
            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sends = passwords.Select((password, i) => Task.Run(async () =>
            {
                await go.Task;
                return (await LoginAsync(clients[i], email, password)).Status;
            })).ToArray();
            go.SetResult();
            return await Task.WhenAll(sends);
        }
        finally
        {
            DisposeAll(clients);
        }
    }

    private async Task<HttpStatusCode[]> SendPinEnrolmentsTogetherAsync(string token, string[] passwords)
    {
        var clients = CreateClients(passwords.Length);
        try
        {
            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sends = passwords.Select((password, i) => Task.Run(async () =>
            {
                await go.Task;
                using var response = await EnrolPinAsync(clients[i], token, password);
                return response.StatusCode;
            })).ToArray();
            go.SetResult();
            return await Task.WhenAll(sends);
        }
        finally
        {
            DisposeAll(clients);
        }
    }

    /// <summary>
    /// Made one after another on the test's thread, never inside the burst: the factory adds every
    /// client it makes to a list without a lock, and two adds at once can leave in it a null that the
    /// factory's <c>Dispose</c> throws on, after every assertion has passed.
    /// </summary>
    private HttpClient[] CreateClients(int count) => [.. Enumerable.Range(0, count).Select(_ => _host!.CreateClient())];

    private static void DisposeAll(HttpClient[] clients)
    {
        foreach (var client in clients)
        {
            client.Dispose();
        }
    }

    private async Task<HttpResponseMessage> EnrolPinAsync(string token, string password)
    {
        using var client = _host!.CreateClient();
        return await EnrolPinAsync(client, token, password);
    }

    private static async Task<HttpResponseMessage> EnrolPinAsync(HttpClient client, string token, string password)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.PostAsJsonAsync(
            "/api/auth/pin", new SetPinRequest { Pin = "424242", Password = password }, Json);
    }

    private static async Task<Answer> LoginAsync(HttpClient client, string email, string password)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest { Email = email, Password = password }, Json);
        return new Answer(
            response.StatusCode, await response.Content.ReadAsStringAsync(), AnswerHeaders.Of(response));
    }

    private static async Task<string> RegisterAsync(HttpClient client) =>
        (await RegisterCoreAsync(client)).Email;

    private static async Task<string> RegisterForTokenAsync(HttpClient client) =>
        (await RegisterCoreAsync(client)).Token;

    private static async Task<(string Email, string Token)> RegisterCoreAsync(HttpClient client)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var email = $"guessbound{unique}@example.com";
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"guessbound_{unique}",
            Email = email,
            Password = Password,
            FirstName = "Guess",
            LastName = "Bound",
        }, Json);
        response.EnsureSuccessStatusCode();
        var registered = (await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json))!.Data!;
        return (email, registered.Token.AccessToken);
    }

    /// <summary>The body without its trace id, written without braces so that a failure can be read.</summary>
    private static string Comparable(string body) => ComparableText.Of(ProblemBody.WithoutTraceId(body));

    private static string[] WrongPasswords(int count) =>
        [.. Enumerable.Range(0, count).Select(i => $"Wrong{i:D2}Pass!")];

    private static string Tally(IEnumerable<HttpStatusCode> answers) =>
        string.Join(", ", answers.GroupBy(a => (int)a).OrderBy(g => g.Key).Select(g => $"{g.Key} x{g.Count()}"));

    public void Dispose()
    {
        _host?.Dispose();
        _factory?.Dispose();
    }
}
