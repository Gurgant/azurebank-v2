namespace AzureBank.Api.Attributes;

/// <summary>
/// Marks an endpoint whose write, past the attempts it is given on an account's row, answers a
/// busy account: 503 <c>SERVICE_UNAVAILABLE</c> with a wait of one second
/// (<c>AccountBusyException</c>, ADR-0058 D6).
/// </summary>
/// <remarks>
/// <para>
/// <c>ServiceUnavailableResponseTransformer</c> reads it from the endpoint metadata and names the
/// busy account in that operation's 503, so the published contract names it where the code
/// answers it and nowhere else. The marker changes nothing in what the endpoint does: the service
/// behind it raises the exception.
/// </para>
/// <para>
/// Seven endpoints carry it: the deposit, the withdrawal, both transfers, the closure of an
/// account, the change of the primary account and the rename of an account.
/// <c>PublishedErrorContractTests</c> lists the seven against the published document.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AnswersBusyAccountAttribute : Attribute
{
}
