using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AzureBank.Shared.Constants;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;

namespace AzureBank.Tests.Integration;

/// <summary>
/// A payee's handle is read in any letter case. The three requests that take one (the lookup, the
/// authorisation mint and the transfer) lower-case it BEFORE they validate it, so <c>Jane_02ga</c>
/// answers exactly as <c>jane_02ga</c>, and a handle that is not one in any letter case is refused
/// as before. Registration and rename are not here: what a NEW handle may be does not change.
///
/// Every case goes through the real pipeline (<c>CustomWebApplicationFactory</c> runs
/// <c>Program.cs</c>), and the bodies are written as text so the letter case on the wire is the one
/// under test, not one a serialiser could change.
/// </summary>
public sealed class PayeeHandleCaseTests : IntegrationTestBase
{
    public PayeeHandleCaseTests(CustomWebApplicationFactory factory) : base(factory) { }

    private const string Pin = "123456";
    private const decimal Amount = 10m;

    /// <summary>The ways a handle that is not one is wrong, each written with a capital.</summary>
    public static TheoryData<string, string> HandlesThatAreNotHandles => new()
    {
        { "Ja", "too short" },
        { "Jane-02", "a hyphen" },
        { "1Jane", "starts with a digit" },
        { "Abcdefghij01234567890", "21 characters" },
        { "Jane 02", "a space" },
    };

    private static string Capitalise(string handle) => char.ToUpperInvariant(handle[0]) + handle[1..];

    private async Task<(Guid Account, string PayeeTag, Guid PayeeAccount, string PayeeToken)> SenderAndPayeeAsync()
    {
        var (token, _, account) = await RegisterTestUserAsync();
        var (payeeTag, _, payeeAccount, payeeToken) = await RegisterRecipientAsync();
        await DepositAsync(token, account, 100m);
        await SetPinAsync(token, Pin);
        SetAuthHeader(token);
        return (account, payeeTag, payeeAccount, payeeToken);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> LookUpAsync(string handle)
    {
        var response = await Client.GetAsync($"/api/users/{Uri.EscapeDataString(handle)}");
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private static string MintBody(Guid from, string handle) =>
        $$"""{"fromAccountId":"{{from}}","recipientAzureTag":{{JsonSerializer.Serialize(handle)}},"amount":{{Amount}},"pin":"{{Pin}}"}""";

    private static string TransferBody(Guid from, string handle) =>
        $$"""{"fromAccountId":"{{from}}","recipientAzureTag":{{JsonSerializer.Serialize(handle)}},"amount":{{Amount}}}""";

    private async Task<(HttpStatusCode Status, JsonElement Body)> MintAsync(Guid from, string handle)
    {
        var response = await Client.PostAsync(
            "/api/transfers/authorizations",
            new StringContent(MintBody(from, handle), Encoding.UTF8, "application/json"));
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private Task<HttpResponseMessage> TransferAsync(
        Guid from, string handle, Guid authorization, Guid? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/transfers")
        {
            Content = new StringContent(TransferBody(from, handle), Encoding.UTF8, "application/json")
        };
        request.Headers.Add(IdempotencyConstants.HeaderName, (idempotencyKey ?? Guid.NewGuid()).ToString());
        request.Headers.Add(StepUpConstants.HeaderName, authorization.ToString());
        return Client.SendAsync(request);
    }

    /// <summary>The answer without its <c>traceId</c>, which is different on every request.</summary>
    private static string WithoutTraceId(JsonElement body) => string.Join(
        ",", body.EnumerateObject().Where(p => p.Name != "traceId").Select(p => p.Name + "=" + p.Value.GetRawText()));

    private static IEnumerable<string> ErrorKeys(JsonElement body) =>
        body.GetProperty("errors").EnumerateObject().Select(p => p.Name);

    // ---- the lookup: GET /api/users/{azureTag} ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheLookup_AnswersAHandleInAnyLetterCase_AsItsLowerCaseForm(bool allCapitals)
    {
        var (_, payeeTag, _, _) = await SenderAndPayeeAsync();
        var written = allCapitals ? payeeTag.ToUpperInvariant() : Capitalise(payeeTag);

        var lower = await LookUpAsync(payeeTag);
        var mixed = await LookUpAsync(written);

        using var all = new AssertionScope();
        lower.Status.Should().Be(HttpStatusCode.OK);
        mixed.Status.Should().Be(HttpStatusCode.OK, "{0} is the handle {1}", written, payeeTag);
        mixed.Body.GetProperty("data").GetProperty("exists").GetBoolean().Should().BeTrue();
        mixed.Body.GetProperty("data").GetProperty("azureTag").GetString()
            .Should().Be(payeeTag, "the answer names the handle the store holds");
        mixed.Body.GetRawText().Should().Be(lower.Body.GetRawText());
    }

    [Fact]
    public async Task TheLookup_OfAHandleNobodyHolds_AnswersTheSameInAnyLetterCase()
    {
        await SenderAndPayeeAsync();

        var lower = await LookUpAsync("nobody_has_this");
        var mixed = await LookUpAsync("Nobody_Has_THIS");

        using var all = new AssertionScope();
        mixed.Status.Should().Be(HttpStatusCode.OK);
        mixed.Body.GetProperty("data").GetProperty("exists").GetBoolean().Should().BeFalse();
        mixed.Body.GetRawText().Should().Be(lower.Body.GetRawText());
    }

    [Theory]
    [MemberData(nameof(HandlesThatAreNotHandles))]
    public async Task TheLookup_StillRefusesAHandleThatIsNotOne_WhateverItsLetterCase(string handle, string because)
    {
        await SenderAndPayeeAsync();

        var asWritten = await LookUpAsync(handle);
        var inLower = await LookUpAsync(handle.ToLowerInvariant());

        using var all = new AssertionScope();
        asWritten.Status.Should().Be(HttpStatusCode.BadRequest, because);
        ErrorKeys(asWritten.Body).Should().Equal("azureTag");
        asWritten.Body.TryGetProperty("data", out _).Should().BeFalse("nothing was looked up");
        asWritten.Body.GetProperty("errors").GetRawText().Should().Be(inLower.Body.GetProperty("errors").GetRawText());
    }

    // ---- the mint: POST /api/transfers/authorizations ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheMint_TakesAHandleInAnyLetterCase_AsItsLowerCaseForm(bool allCapitals)
    {
        var (account, payeeTag, _, _) = await SenderAndPayeeAsync();
        var written = allCapitals ? payeeTag.ToUpperInvariant() : Capitalise(payeeTag);

        var mixed = await MintAsync(account, written);

        using var all = new AssertionScope();
        mixed.Status.Should().Be(HttpStatusCode.Created, "{0} is the handle {1}", written, payeeTag);
        mixed.Body.GetProperty("data").GetProperty("authorizationId").GetGuid().Should().NotBeEmpty();
    }

    [Fact]
    public async Task TheMint_OfAHandleNobodyHolds_AnswersTheSameInAnyLetterCase()
    {
        var (account, _, _, _) = await SenderAndPayeeAsync();

        var lower = await MintAsync(account, "nobody_has_this");
        var mixed = await MintAsync(account, "Nobody_Has_THIS");

        using var all = new AssertionScope();
        lower.Status.Should().Be(HttpStatusCode.NotFound);
        mixed.Status.Should().Be(lower.Status);
        WithoutTraceId(mixed.Body).Should().Be(WithoutTraceId(lower.Body));
    }

    [Theory]
    [MemberData(nameof(HandlesThatAreNotHandles))]
    public async Task TheMint_StillRefusesAHandleThatIsNotOne_WhateverItsLetterCase(string handle, string because)
    {
        var (account, _, _, _) = await SenderAndPayeeAsync();

        var asWritten = await MintAsync(account, handle);
        var inLower = await MintAsync(account, handle.ToLowerInvariant());

        using var all = new AssertionScope();
        asWritten.Status.Should().Be(HttpStatusCode.BadRequest, because);
        ErrorKeys(asWritten.Body).Should().NotBeEmpty();
        asWritten.Body.GetProperty("errors").GetRawText().Should().Be(inLower.Body.GetProperty("errors").GetRawText());
    }

    // ---- the transfer: POST /api/transfers ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheTransfer_TakesAHandleInAnyLetterCase_AndPaysThePayeeTheLowerCaseHandleHolds(bool allCapitals)
    {
        var (account, payeeTag, payeeAccount, payeeToken) = await SenderAndPayeeAsync();
        var written = allCapitals ? payeeTag.ToUpperInvariant() : Capitalise(payeeTag);
        var authorization = (await MintAsync(account, written)).Body
            .GetProperty("data").GetProperty("authorizationId").GetGuid();

        var response = await TransferAsync(account, written, authorization);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        using var all = new AssertionScope();
        response.StatusCode.Should().Be(HttpStatusCode.Created, "{0} is the handle {1}", written, payeeTag);
        body.GetProperty("data").GetProperty("recipientAzureTag").GetString()
            .Should().Be(payeeTag, "the receipt names the handle the store holds");
        (await BalanceOfAsync(payeeToken, payeeAccount)).Should().Be(Amount);
    }

    [Fact]
    public async Task TheTransfer_AuthorisedInOneLetterCase_IsAcceptedInAnother()
    {
        // The authorisation is bound to the payee, not to the way the handle was spelled.
        var (account, payeeTag, _, _) = await SenderAndPayeeAsync();
        var authorization = (await MintAsync(account, payeeTag)).Body
            .GetProperty("data").GetProperty("authorizationId").GetGuid();

        var response = await TransferAsync(account, payeeTag.ToUpperInvariant(), authorization);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Theory]
    [MemberData(nameof(HandlesThatAreNotHandles))]
    public async Task TheTransfer_StillRefusesAHandleThatIsNotOne_WhateverItsLetterCase(string handle, string because)
    {
        var (account, _, _, _) = await SenderAndPayeeAsync();

        var asWritten = await TransferAsync(account, handle, Guid.NewGuid());
        var inLower = await TransferAsync(account, handle.ToLowerInvariant(), Guid.NewGuid());
        var asWrittenBody = await asWritten.Content.ReadFromJsonAsync<JsonElement>();
        var inLowerBody = await inLower.Content.ReadFromJsonAsync<JsonElement>();

        using var all = new AssertionScope();
        asWritten.StatusCode.Should().Be(HttpStatusCode.BadRequest, because);
        ErrorKeys(asWrittenBody).Should().NotBeEmpty();
        asWrittenBody.GetProperty("errors").GetRawText().Should().Be(inLowerBody.GetProperty("errors").GetRawText());
    }

    [Fact]
    public async Task ARetryOfATransferInAnotherLetterCase_IsAnotherRequest_ForTheIdempotencyKey()
    {
        // The fingerprint is over the bytes sent, so the same key with the handle spelled another
        // way is a different request and is refused like any other change to the body.
        var (account, payeeTag, _, _) = await SenderAndPayeeAsync();
        var authorization = (await MintAsync(account, payeeTag)).Body
            .GetProperty("data").GetProperty("authorizationId").GetGuid();
        var key = Guid.NewGuid();

        var first = await TransferAsync(account, payeeTag, authorization, key);
        var sameBytes = await TransferAsync(account, payeeTag, authorization, key);
        var otherCase = await TransferAsync(account, payeeTag.ToUpperInvariant(), authorization, key);

        using var all = new AssertionScope();
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        sameBytes.StatusCode.Should().Be(HttpStatusCode.Created, "the same bytes replay the first answer");
        otherCase.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await otherCase.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString()
            .Should().Be(ErrorCodes.IdempotencyKeyReuse, "a key reused with a different body");
    }

    // ---- only A to Z is lower-cased ----

    /// <summary>
    /// A handle that exists, and the same handle with its first letter swapped for a character that
    /// <c>ToLowerInvariant</c> would turn into that letter: the Kelvin sign U+212A for k, the dotted
    /// capital I U+0130 for i.
    /// </summary>
    private async Task<string> RegisterHandleAsync(string handle)
    {
        var response = await Client.PostAsJsonAsync("/api/auth/register", new
        {
            azureTag = handle,
            email = $"{handle}@example.com",
            password = TestUserPassword,
            firstName = "Look",
            lastName = "Alike"
        });
        response.EnsureSuccessStatusCode();
        return handle;
    }

    public static TheoryData<string, string> LettersOnlyUnicodeLowerCases => new()
    {
        { "K", "k" },
        { "İ", "i" },
    };

    [Theory]
    [MemberData(nameof(LettersOnlyUnicodeLowerCases))]
    public async Task ACharacterOutsideAToZ_IsNotLowerCased_SoItDoesNotReachAHandleThatExists(
        string impostor, string letter)
    {
        var (account, _, _, _) = await SenderAndPayeeAsync();
        var held = await RegisterHandleAsync($"{letter}ook_{Guid.NewGuid():N}"[..14]);
        var written = impostor + held[1..];

        var lookup = await LookUpAsync(written);
        var mint = await MintAsync(account, written);
        var transfer = await TransferAsync(account, written, Guid.NewGuid());
        var transferBody = await transfer.Content.ReadFromJsonAsync<JsonElement>();
        var control = await LookUpAsync(held);

        using var all = new AssertionScope();
        control.Status.Should().Be(HttpStatusCode.OK, "the plain handle exists");
        control.Body.GetProperty("data").GetProperty("exists").GetBoolean().Should().BeTrue();
        lookup.Status.Should().Be(HttpStatusCode.BadRequest, "{0} is not made of the characters of a handle", written);
        ErrorKeys(lookup.Body).Should().Equal("azureTag");
        mint.Status.Should().Be(HttpStatusCode.BadRequest);
        ErrorKeys(mint.Body).Should().NotBeEmpty();
        transfer.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ErrorKeys(transferBody).Should().NotBeEmpty();
    }

    [Fact]
    public async Task TheSendersOwnHandleInCapitals_IsStillASelfTransfer_LikeInLowerCase()
    {
        var (token, _, account) = await RegisterTestUserAsync();
        await DepositAsync(token, account, 100m);
        await SetPinAsync(token, Pin);
        SetAuthHeader(token);
        var me = await Client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        var own = me.GetProperty("data").GetProperty("azureTag").GetString()!;

        var lower = await MintAsync(account, own);
        var capitals = await MintAsync(account, own.ToUpperInvariant());

        using var all = new AssertionScope();
        lower.Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        lower.Body.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.SelfTransferNotAllowed);
        capitals.Status.Should().Be(lower.Status);
        capitals.Body.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.SelfTransferNotAllowed);
    }

    private async Task<decimal> BalanceOfAsync(string token, Guid accountId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/accounts/{accountId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("balance").GetDecimal();
    }
}
