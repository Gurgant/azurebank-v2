namespace AzureBank.Api.Attributes;

/// <summary>
/// Marks an endpoint that exists only on the public demo. While <c>Demo:Enabled</c> is false,
/// <c>DemoEndpointMiddleware</c> reads it from the endpoint metadata and answers 404, as for a
/// path that matches no route.
/// </summary>
/// <remarks>
/// A marker rather than a check inside the action, for the reason
/// <see cref="TokenEndpointAttribute"/> is one: the refusal happens in middleware before model
/// binding, so a deployment with the demo off answers the same 404 whatever the request's body.
/// Checked in the action, a malformed body would be answered 400 and a form post 415 first, and
/// either tells the caller that the endpoint is there.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DemoOnlyAttribute : Attribute
{
}
