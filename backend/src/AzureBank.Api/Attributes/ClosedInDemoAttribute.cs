namespace AzureBank.Api.Attributes;

/// <summary>
/// Marks an endpoint the public demo closes. While <c>Demo:Enabled</c> is true,
/// <c>DemoEndpointMiddleware</c> reads it from the endpoint metadata and refuses the request with
/// 403, whatever it carries.
/// </summary>
/// <remarks>
/// <para>
/// One endpoint carries it, registration, and the refusal is registration's own: 403 with the code
/// <c>REGISTRATION_CLOSED</c>. On the demo a visitor is handed a prepared copy and nobody creates a
/// user.
/// </para>
/// <para>
/// A marker rather than a check inside the action, for the reason <see cref="DemoOnlyAttribute"/>
/// is one: the refusal happens in middleware before model binding, so it is the same 403 whatever
/// the request's body. Checked in the action, a malformed body would be answered 400 and a form
/// post 415 first, each telling the caller what a registration should look like on a deployment
/// that takes none.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ClosedInDemoAttribute : Attribute
{
}
