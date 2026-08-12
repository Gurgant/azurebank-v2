using System.Linq;
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
    public async Task AMalformedCurrentPinIsAValidationFailure_NotAWrongPin()
    {
        // Model validation runs before the action, so a currentPin that cannot BE a PIN never
        // reaches the verifier and must not be reported as an incorrect one.
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        await PostSetPin("123456", null);

        var response = await PostSetPin("999999", currentPin: "abc");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        // The key the errors dict is under — measured, because the mock has to reproduce it and a
        // guess would be exactly the drift this whole change is about.
        body.GetProperty("errors").EnumerateObject().Select(p => p.Name)
            .Should().Contain("CurrentPin");
    }

    [Fact]
    public async Task AMalformedCurrentPinIsRejectedEvenWhenENROLLING()
    {
        /*
          Validation runs BEFORE the action, so it does not know whether a PIN exists — a supplied
          currentPin must be well-formed even on the enrolment path, where the value is otherwise
          ignored. Easy to get wrong the other way round: putting the format check inside the
          "already has a PIN" branch makes it state-dependent, which the real pipeline is not.
        */
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        var response = await PostSetPin("123456", currentPin: "abc");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WrongCurrentPinsTripTheSameLockoutAsAnyOtherWrongPin()
    {
        // The reason CurrentPin goes through IPinVerifier rather than the hasher: without it this
        // endpoint would be an uncounted brute-force oracle, which is worse than the hole it closes.
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        await PostSetPin("123456", null);

        /*
          Assert EVERY attempt, not just the last. Checking only the final response cannot tell
          "locks on the third" from "locks on the first" — both end in 429, and a lockout that fired
          immediately would look identical while being a different (and worse) behaviour.
        */
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var response = await PostSetPin("999999", currentPin: "000000");

            if (attempt < 3)
            {
                response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                    "attempt {0} is under the threshold", attempt);
                (await ErrorCodeOf(response)).Should().Be(ErrorCodes.InvalidPin);
            }
            else
            {
                response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
                    "the third crosses it");
                (await ErrorCodeOf(response)).Should().Be(ErrorCodes.PinLocked);
            }
        }
    }

    [Fact]
    public async Task ASuccessfulChangeClearsTheFailureCounter()
    {
        /*
          Observable without reading the database: fail twice (counter 2), then change the PIN with
          the CORRECT current one — which resets the counter — then fail once more. If the reset
          held, that last one is attempt 1 and answers 401. If the counter was resurrected, it is
          attempt 3 and answers 429.

          Worth pinning because the reset happens in PinService's own DbContext while SetPinAsync
          holds a separately tracked user, and UpdateAsync writes that tracked instance back.
        */
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        await PostSetPin("123456", null);

        (await PostSetPin("999999", currentPin: "000000")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await PostSetPin("999999", currentPin: "000000")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        (await PostSetPin("654321", currentPin: "123456")).StatusCode
            .Should().Be(HttpStatusCode.OK, "the correct current PIN succeeds and resets the count");

        var afterReset = await PostSetPin("111111", currentPin: "000000");

        afterReset.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a cleared counter makes this attempt 1, not attempt 3");
        (await ErrorCodeOf(afterReset)).Should().Be(ErrorCodes.InvalidPin);
    }

    [Fact]
    public async Task AnEmptyCurrentPinIsAValidationFailure()
    {
        // "" is not "absent": the [Pin] annotation runs at binding and rejects it, so this is a 400
        // before the action — NOT the 422 that a genuinely missing value produces.
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        await PostSetPin("123456", null);

        var response = await PostSetPin("999999", currentPin: "");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ALockedPinCannotBeReplacedEvenWithTheCorrectCurrentOne()
    {
        // Lockout is checked before the comparison, so knowing the PIN does not lift it. Otherwise
        // replacement would be a way to clear a lock that guessing had earned.
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        await PostSetPin("123456", null);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await PostSetPin("999999", currentPin: "000000");
        }

        var response = await PostSetPin("999999", currentPin: "123456");

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.PinLocked);
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
