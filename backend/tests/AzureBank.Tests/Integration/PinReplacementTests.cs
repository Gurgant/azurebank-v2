using System.Net;
using System.Net.Http.Json;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Tests.Fixtures;
using FluentAssertions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Changing a PIN requires the current one. Enrolling does not.
///
/// <para>
/// Until this guard existed, <c>POST /api/auth/pin</c> overwrote <c>PinHash</c> unconditionally, so
/// holding a session was enough to REPLACE the PIN and then satisfy every gate the PIN protects.
/// Measured end to end through the BFF on the running stack, cookie only, no token ever visible to
/// the caller:
/// </para>
/// <code>
/// register             -> 201, authLevel 1
/// set-pin  "131313"    -> 200      (enrolment)
/// set-pin  "999999"    -> 200      &lt;- no proof of "131313" required
/// verify-pin "999999"  -> 200, authLevel 2
/// GET .../full-number  -> 200      "AB-3142-8079-89", unmasked
/// </code>
/// <para>
/// Two protections were nullified at once, and neither could have stopped it. ADR-0010's
/// attempt-limiting never engaged because nothing was guessed. ADR-0008's step-up gate checks that
/// A PIN was entered, never that it was the user's — so the elevated session was, by its own rules,
/// legitimate. The only place the rule can live is the point of replacement.
/// </para>
/// <para>
/// Not one existing test failed when the guard was added, which is why this shipped: the suite
/// covered enrolling a PIN and verifying a PIN, and never covered changing one.
/// </para>
/// </summary>
public class PinReplacementTests : IntegrationTestBase
{
    public PinReplacementTests(CustomWebApplicationFactory factory) : base(factory) { }

    /// <summary>Posts set-pin directly, so the optional CurrentPin can be omitted or wrong.
    /// Named apart from the base helper deliberately — same name, different arity hid it (CS0108).</summary>
    private async Task<HttpResponseMessage> PostSetPin(string pin, string? currentPin) =>
        await Client.PostAsJsonAsync("/api/auth/pin",
            new SetPinRequest { Pin = pin, CurrentPin = currentPin }, JsonOptions);

    private async Task<string?> ErrorCodeOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return body.TryGetProperty("errorCode", out var code) ? code.GetString() : null;
    }

    /// <summary>
    /// The `verified` flag from POST /api/auth/pin/verify, which answers 200 either way.
    /// </summary>
    private async Task<bool> VerifiedFlag(string pin)
    {
        var response = await Client.PostAsJsonAsync("/api/auth/pin/verify",
            new VerifyPinRequest { Pin = pin }, JsonOptions);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return body.GetProperty("data").GetProperty("verified").GetBoolean();
    }

    [Fact]
    public async Task EnrollingAFirstPinNeedsNoCurrentPin()
    {
        // The account password already gated getting here, and there is nothing to prove yet.
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        var response = await PostSetPin("123456", currentPin: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ReplacingAPinWithoutTheCurrentOneIsRefused()
    {
        // THE ATTACK. Before the guard this returned 200 and the PIN became the caller's choice.
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        (await PostSetPin("123456", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await PostSetPin("999999", currentPin: null);

        // 422: "required only once a PIN exists" is a rule the schema cannot express, which is the
        // split BusinessRuleException documents.
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.PinRequired);
    }

    [Fact]
    public async Task ReplacingAPinWithTheWrongCurrentOneIsRefused()
    {
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        await PostSetPin("123456", null);

        var response = await PostSetPin("999999", currentPin: "000000");

        // Same shape withdraw returns for a bad PIN, so the two step-up paths answer alike.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.InvalidPin);
    }

    [Fact]
    public async Task ARefusedReplacementLeavesTheOldPinWorking()
    {
        // The assertion that matters: a rejected attempt must not have moved the hash. Asserting
        // only the status of the refusal would pass even if the write had gone through first.
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        await PostSetPin("123456", null);

        await PostSetPin("999999", currentPin: null);
        await PostSetPin("999999", currentPin: "000000");

        /*
          Assert on the `verified` FLAG, not the status. This endpoint answers 200 for a wrong PIN
          and carries the outcome in the body — see AuthEndpointTests.
          VerifyPin_WithIncorrectPin_ReturnsOkWithVerifiedFalse. A first draft here asserted
          "not 200" and failed against the real contract, which is the right way round: the code
          was correct and the assumption was not.
        */
        (await VerifiedFlag("123456")).Should().BeTrue("a refused replacement must not move the hash");
        (await VerifiedFlag("999999")).Should().BeFalse("the attacker's choice must never take");
    }

    [Fact]
    public async Task ReplacingAPinWithTheCorrectCurrentOneSucceedsAndTakesEffect()
    {
        // The legitimate path still works — a guard that blocked everything would also pass the
        // three tests above.
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        await PostSetPin("123456", null);

        var response = await PostSetPin("654321", currentPin: "123456");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await VerifiedFlag("654321")).Should().BeTrue();
        (await VerifiedFlag("123456")).Should().BeFalse("the replaced PIN must stop working");
    }
}
