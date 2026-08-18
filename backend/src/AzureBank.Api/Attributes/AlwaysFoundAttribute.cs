namespace AzureBank.Api.Attributes;

/// <summary>
/// Marks a lookup endpoint that has a path parameter and yet CANNOT answer 404, so that
/// <c>NotFoundResponseTransformer</c> does not invent one for it.
/// </summary>
/// <remarks>
/// <para>
/// That transformer documents a 404 on every operation whose route template contains a <c>{</c>,
/// which is a good guess and, for exactly one endpoint here, a wrong one. <c>GET /api/users/{azureTag}</c>
/// answers an unknown handle with <b>200</b> and <c>exists: false</c> — measured, not assumed:
/// <c>{"data":{"azureTag":"nobody_here_at_all","displayName":"","exists":false},"message":null}</c>.
/// That is the deliberate enumeration-neutral confirmation oracle of ADR-0014: a 404 would tell an
/// authenticated caller which handles are real, which is the customer list this design refuses to
/// hand out one probe at a time.
/// </para>
/// <para>
/// So the published 404 was a response the code cannot produce. A client written against it would
/// branch on a status that never arrives and treat "no such user" as success — the same defect as an
/// undocumented body, pointed the other way, and the reason it is deleted rather than given one.
/// </para>
/// <para>
/// MEASURE BEFORE APPLYING THIS. It asserts a fact about runtime behaviour, and nothing checks it.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AlwaysFoundAttribute : Attribute
{
}
