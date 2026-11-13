using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Services.Implementations;
using AzureBank.Shared.Services.Interfaces;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// What a write of the user's row that began earlier does to a rename of the handle and to a PIN
/// change that commit while it runs.
/// </summary>
/// <remarks>
/// <para>
/// A first PIN loads the user, spends the hash time, and saves the whole row with the concurrency
/// stamp it read. A rename that commits in between is in the database and not in that row: unless
/// it moves the stamp, the save matches and puts the old handle back. The PIN's own re-hash on first
/// use after a change of pepper key is the same shape: it must write only over the hash it verified,
/// or it puts back the hash of the PIN that a change has since replaced.
/// </para>
/// <para>
/// No race is run, as in <see cref="PasswordRehashWriteSqlServerTests"/>: what the other request
/// does is done inside the verification, after the load and before the write, every time. Only a
/// relational store has the two as two statements, hence the database named by
/// <c>AZUREBANK_TEST_SQLSERVER</c>.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class UserRowConcurrentWriteSqlServerTests : IDisposable
{
    private const string Password = "TestPass123!";
    private const string Pin = "424242";
    private const string ReplacementPin = "737373";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private CustomWebApplicationFactory? _factory;
    private WebApplicationFactory<Program>? _host;

    public void Dispose()
    {
        _host?.Dispose();
        _factory?.Dispose();
    }

    [SqlServerFact]
    public async Task AFirstPinThatBeganBeforeAHandleRename_FailsAndLeavesTheNewHandle()
    {
        var hasher = new RoundCountingPasswordHasher();
        var (client, email) = await RegisterAsync(services =>
        {
            services.RemoveAll<IPasswordHasher<ApplicationUser>>();
            services.AddSingleton<IPasswordHasher<ApplicationUser>>(hasher);
        });
        var before = await StoredAsync(email);
        var newTag = $"renamed_{Guid.NewGuid():N}"[..16];

        // The password is verified after the user was loaded and before the PIN is saved.
        hasher.BeforeTheNextAnswer(() => InAScope(services => services
            .GetRequiredService<IUserService>()
            .RenameAzureTagAsync(before.Id, newTag)));
        var status = await SetPinAsync(client, Password);

        var after = await StoredAsync(email);
        using var all = new AssertionScope();
        before.AzureTag.Should().NotBe(newTag, "ARRANGE");
        status.Should().Be(HttpStatusCode.UnprocessableEntity, "the save of the PIN lost to the rename");
        after.AzureTag.Should().Be(newTag, "a row loaded before the rename is not this request's to write back");
        after.PinHash.Should().BeNullOrEmpty("the PIN that lost was not stored");
    }

    [SqlServerFact]
    public async Task APinCheckThatBeganBeforeAPinChange_DoesNotWriteTheOldPinOverTheNewHash()
    {
        var hasher = new RewritingPinHasher();
        var (client, email) = await RegisterAsync(hasher.InstallIn);
        (await SetPinAsync(client, Password)).Should().Be(HttpStatusCode.OK, "ARRANGE: the user has a PIN");

        // A hash from before the pepper, so the check below has an upgrade to write.
        var unpeppered = new PasswordHasher().HashPin(Pin);
        var before = await StoredAsync(email);
        await using (var scope = _host!.Services.CreateAsyncScope())
        {
            var written = await scope.ServiceProvider.GetRequiredService<AzureBankDbContext>()
                .Users.Where(u => u.Id == before.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.PinHash, unpeppered));
            written.Should().Be(1, "ARRANGE: the hash of the old PIN, before the pepper, is in the row");
        }
        hasher.PinNeedsRehash(unpeppered).Should().BeTrue("ARRANGE: the check has an upgrade to write");

        // The PIN is changed after the check read the hash and compared the PIN, and before the
        // upgrade is written.
        var ofTheReplacement = hasher.HashPin(ReplacementPin);
        hasher.AfterTheNextMatch(() => InAScope(services => services
            .GetRequiredService<AzureBankDbContext>().Users
            .Where(u => u.Id == before.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PinHash, ofTheReplacement))));
        bool verified;
        await using (var scope = _host!.Services.CreateAsyncScope())
        {
            verified = await scope.ServiceProvider.GetRequiredService<IPinVerifier>().VerifyPinAsync(before.Id, Pin);
        }

        var after = await StoredAsync(email);
        using var all = new AssertionScope();
        verified.Should().BeTrue("it proved the PIN of the hash it loaded");
        after.PinHash.Should().NotBeNullOrEmpty();
        hasher.VerifyPin(after.PinHash!, ReplacementPin).Should().BeTrue("the PIN stored since the load stays the PIN");
        hasher.VerifyPin(after.PinHash!, Pin).Should().BeFalse("the old PIN is not put back");
    }

    private async Task<(HttpClient Client, string Email)> RegisterAsync(Action<IServiceCollection> configure)
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(configure));
        var client = _host.CreateClient();

        var unique = Guid.NewGuid().ToString("N")[..8];
        var email = $"rowwrite{unique}@example.com";
        using var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"rowwrite_{unique}",
            Email = email,
            Password = Password,
            FirstName = "Row",
            LastName = "Write",
        }, Json);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", result!.Data!.Token.AccessToken);
        return (client, email);
    }

    private static async Task<HttpStatusCode> SetPinAsync(HttpClient client, string password)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/auth/pin", new SetPinRequest { Pin = Pin, Password = password }, Json);
        return response.StatusCode;
    }

    /// <summary>
    /// Runs <paramref name="work"/> in a scope of its own, as another request would have one, and
    /// waits for it: the verification it is called from is not asynchronous.
    /// </summary>
    private void InAScope(Func<IServiceProvider, Task> work)
    {
        using var scope = _host!.Services.CreateScope();
        work(scope.ServiceProvider).GetAwaiter().GetResult();
    }

    private async Task<ApplicationUser> StoredAsync(string email)
    {
        await using var scope = _host!.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AzureBankDbContext>()
            .Users.AsNoTracking().SingleAsync(u => u.Email == email);
    }

    /// <summary>
    /// The API's own PIN hasher, running what <see cref="AfterTheNextMatch"/> was given once, after
    /// a PIN has matched its hash: in a check of the PIN that is after the user was loaded and
    /// before anything is written.
    /// </summary>
    private sealed class RewritingPinHasher : IPasswordHasher
    {
        private IPasswordHasher? _inner;
        private Action? _afterTheNextMatch;

        /// <summary>What the next matching comparison runs, once, after it has matched.</summary>
        public void AfterTheNextMatch(Action run) => Volatile.Write(ref _afterTheNextMatch, run);

        /// <summary>Puts this hasher in front of the one <paramref name="services"/> registers.</summary>
        public void InstallIn(IServiceCollection services)
        {
            var real = services.Single(d => d.ServiceType == typeof(IPasswordHasher));
            services.Remove(real);
            services.AddSingleton<IPasswordHasher>(provider =>
            {
                _inner = (IPasswordHasher)real.ImplementationFactory!(provider);
                return this;
            });
        }

        public string HashPin(string pin) => _inner!.HashPin(pin);

        public bool VerifyPin(string hash, string pin)
        {
            var matched = _inner!.VerifyPin(hash, pin);
            if (matched)
            {
                Interlocked.Exchange(ref _afterTheNextMatch, null)?.Invoke();
            }

            return matched;
        }

        public bool PinNeedsRehash(string hash) => _inner!.PinNeedsRehash(hash);

        public bool PinPepperMissingFor(string hash) => _inner!.PinPepperMissingFor(hash);
    }
}
