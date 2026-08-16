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
/// </summary>
/// <param name="FromAccountId">The account the money leaves.</param>
/// <param name="ToAccountId">The destination account for an internal transfer; null for an external one, where the payee is a person and their receiving account is resolved server-side.</param>
/// <param name="RecipientUserId">The payee for an external transfer; null for an internal one, where the payee is the payer.</param>
/// <param name="Amount">The amount, bound at the stored money scale.</param>
public readonly record struct StepUpBinding(
    Guid FromAccountId,
    Guid? ToAccountId,
    Guid? RecipientUserId,
    decimal Amount);
