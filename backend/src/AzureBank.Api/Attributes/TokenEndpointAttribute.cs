namespace AzureBank.Api.Attributes;

/// <summary>
/// Marks one of the token endpoints — login, register, refresh, revoke and logout — or the stamp
/// feed, session-stamps (06 §5.3), which answer only the BFF's own client, over loopback (06 §4.2).
/// <c>TokenRoadMiddleware</c> reads it from the endpoint metadata and answers anything else 404.
/// </summary>
/// <remarks>
/// A marker rather than a filter, for the reason <see cref="RequireIdempotencyAttribute"/> is one: the
/// refusal happens in middleware before model binding, so a caller off the road learns nothing from
/// a validation answer about a body it should not have been able to send.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TokenEndpointAttribute : Attribute
{
}
