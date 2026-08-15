namespace AzureBank.Shared.Options;

/// <summary>
/// Configuration for step-up authorisations (ADR-0042).
/// Binds to the "StepUp" section in appsettings.json.
/// </summary>
public class StepUpOptions
{
    /// <summary>Configuration section name in appsettings.json.</summary>
    public const string SectionName = "StepUp";

    /// <summary>
    /// Server-side key for the HMAC-SHA256 binding. MUST be configured (user-secrets /
    /// environment), never committed and never stored in the database — the bound fields are
    /// <c>(operation, user, source account, payee, amount)</c>, which is a small enough space that an
    /// unkeyed digest would let anyone with database read access confirm guesses about who paid whom.
    ///
    /// <para>
    /// Separate from <see cref="IdempotencyOptions.HashKey"/> on purpose: the two hashes answer
    /// different questions, and one key compromised should not forge the other's answer.
    /// </para>
    /// </summary>
    public string BindingKey { get; set; } = string.Empty;

    /// <summary>
    /// How long a minted authorisation stays spendable. Short by design, and not refreshable: it
    /// exists only to cross the gap between the PIN being proved and the operation being performed,
    /// and because the client sends on the sixth digit that gap is normally milliseconds. The window
    /// is therefore only ever consumed by a retry after a lost response.
    ///
    /// <para>
    /// Not a regulatory figure. The RTS prescribes no lifetime for an authentication code — EBA Q&amp;A
    /// 2018_4141: "The Commission Delegated Regulation does not prescribe a time limit for the
    /// provision of the two authentication elements necessary for SCA while within a session." The
    /// five minutes in Art. 4(3)(d) is an inactivity limit on account access, a different provision.
    /// Two minutes is our policy. Default: 2 minutes.
    /// </para>
    /// </summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(2);
}
