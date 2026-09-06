namespace AzureBank.Api.Services;

/// <summary>
/// The fields that define an operation for binding purposes (ADR-0042, PSD2-RTS Art. 5(1)(b)).
///
/// <para>
/// <paramref name="RecipientUserId"/> and NOT the AzureTag: the handle is renameable (ADR-0015), so
/// an authorisation bound to <c>@admin</c> would survive <c>@admin</c> becoming someone else's
/// handle — the binding would outlive the payee it named. The tag stays a display concern.
/// </para>
///
/// <para>
/// The description is deliberately NOT bound. Art. 5(1)(d) invalidates on a change to "the amount or
/// the payee", and a different memo moves the same money to the same person.
/// </para>
///
/// <para>
/// Lives beside <c>TokenResult</c> and <c>RefreshRotationResult</c> rather than in
/// <c>Services.Interfaces</c>, because <c>NamingConventionTests.ServiceInterfaces_ShouldStartWithI</c>
/// requires every type in that namespace to be an interface — and it caught this one.
/// </para>
///
/// <para>
/// REINTERPRETED FOR A NON-MONEY OPERATION (ADR-0049), without adding a field: for an account
/// closure <paramref name="FromAccountId"/> is the account the operation is about, and
/// <paramref name="Amount"/> is 0 because no money moves. The four fields keep their positions and
/// their rendering, so the <c>v1</c> payload is unchanged and every transfer authorisation minted
/// before closures joined the rail stays valid — the version bump is for ADDING a bound field, and
/// none was added. Build a closure's binding with <see cref="ForAccountDeletion"/> rather than by
/// hand, so the mint and the validation cannot disagree about which fields a closure fills.
/// </para>
/// </summary>
/// <param name="FromAccountId">The account the money leaves — or, for a closure, the account being closed.</param>
/// <param name="ToAccountId">The destination account for an internal transfer; null for an external one, where the payee is a person and their receiving account is resolved server-side, and null for a closure.</param>
/// <param name="RecipientUserId">The payee for an external transfer; null for an internal one, where the payee is the payer, and null for a closure.</param>
/// <param name="Amount">The amount, bound at the stored money scale; 0 for a non-money operation.</param>
public readonly record struct StepUpBinding(
    Guid FromAccountId,
    Guid? ToAccountId,
    Guid? RecipientUserId,
    decimal Amount)
{
    /// <summary>
    /// The binding of a closure of <paramref name="accountId"/>: the account, no counterparty, and
    /// an amount of 0 (ADR-0049). One factory for the two call sites — the mint at
    /// <c>AccountService.AuthoriseDeletionAsync</c> and the validation at
    /// <c>AccountService.DeleteAccountAsync</c> — so a closure authorisation can never be minted
    /// for a shape the deletion then fails to recompute.
    /// </summary>
    public static StepUpBinding ForAccountDeletion(Guid accountId) => new(accountId, null, null, 0m);
}
