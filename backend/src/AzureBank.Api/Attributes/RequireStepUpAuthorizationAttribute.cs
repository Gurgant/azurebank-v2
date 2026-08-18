namespace AzureBank.Api.Attributes;

/// <summary>
/// Marks an endpoint that refuses without a <c>Step-Up-Authorization</c> header (ADR-0042).
///
/// <para>
/// A DOCUMENTATION marker, not a gate. Enforcement lives in <c>TransferService</c>, downstream of
/// the idempotency replay lookup, because a replay must not be refused for a header it does not
/// need — see the note on <c>TransferService.RequireAuthorization</c>. What this marker buys is that
/// <c>StepUpAuthorizationOperationTransformer</c> can publish the header as <c>required: true</c>.
/// </para>
///
/// <para>
/// Why that needs saying at all: the action binds <c>[FromHeader] Guid?</c>, deliberately, so that
/// an absent header and an empty one both reach the service and both get
/// <c>401 AUTHORIZATION_REQUIRED</c> instead of a model-state 400. Nullability is also what the
/// OpenAPI generator reads to decide whether a parameter is required, so without this marker the
/// published contract would say the header may be omitted while the runtime refuses — a contract
/// wider than the code, which is the defect class the architecture guards exist to prevent, and one
/// that regenerating the spec cannot detect because the generated file and the spec would agree.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireStepUpAuthorizationAttribute : Attribute
{
}
