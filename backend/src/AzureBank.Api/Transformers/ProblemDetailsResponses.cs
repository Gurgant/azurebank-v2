using Microsoft.OpenApi;

namespace AzureBank.Api.Transformers;

/// <summary>
/// One place that declares an error response as the <c>application/json</c> ProblemDetails the API
/// actually sends, for the transformers that fill in 401, 403 and 404.
/// </summary>
/// <remarks>
/// Shared rather than repeated because the alternative is what this codebase already demonstrates:
/// fifteen responses carry hand-written inline error schemas, each subtly its own, because there was
/// no usable shared route. A reference, not a copy — so when
/// <see cref="ProblemDetailsExtensionsTransformer"/> adds a member, every response that uses this
/// gains it.
/// </remarks>
internal static class ProblemDetailsResponses
{
    /// <summary>The media type the API actually answers errors with (measured, not assumed).</summary>
    private const string MediaType = "application/json";

    public static Dictionary<string, OpenApiMediaType> Content() => new()
    {
        [MediaType] = new OpenApiMediaType
        {
            Schema = new OpenApiSchemaReference("ProblemDetails"),
        },
    };

    /// <summary>
    /// Declares <paramref name="statusCode"/> without overwriting anything the endpoint said itself.
    /// </summary>
    /// <param name="responses">The operation's response map, mutated in place.</param>
    /// <param name="statusCode">The status to declare, as the OpenAPI key ("401", "403", "404").</param>
    /// <param name="reasonPhrase">
    /// The HTTP reason phrase for this status. It is exactly what ApiExplorer writes as the
    /// description when nobody wrote one, which is how this can tell a generated placeholder from a
    /// sentence someone meant.
    /// </param>
    /// <param name="description">The sentence to publish when nobody has written one.</param>
    /// <remarks>
    /// <para>
    /// FILL IN, NEVER OVERRIDE. The transformers that call this used to ASSIGN, and it cost real
    /// information: <c>TransferController</c> declares
    /// <c>[ProducesResponseType(typeof(ProblemDetails), 401)]</c> with a comment naming the three
    /// step-up codes it can answer, and the assignment replaced it with an empty body.
    /// </para>
    /// <para>
    /// The description needs the opposite care. Where an endpoint declares the status itself, the
    /// generated description is the bare reason phrase — "Forbidden" — because a
    /// <c>[ProducesResponseType]</c> attribute has nowhere to put prose; only a
    /// <c>&lt;response code="403"&gt;</c> XML comment does. Replacing that placeholder loses nothing;
    /// replacing a real sentence would, so it is left alone.
    /// </para>
    /// </remarks>
    public static void Declare(
        OpenApiResponses responses,
        string statusCode,
        string reasonPhrase,
        string description)
    {
        if (!responses.TryGetValue(statusCode, out var declared)
            || declared is not OpenApiResponse existing)
        {
            responses[statusCode] = new OpenApiResponse
            {
                Description = description,
                Content = Content(),
            };
            return;
        }

        if (string.IsNullOrWhiteSpace(existing.Description)
            || string.Equals(existing.Description.Trim(), reasonPhrase, StringComparison.Ordinal))
        {
            existing.Description = description;
        }

        // Count, not null: an endpoint that declared the status with no type leaves an EMPTY
        // dictionary here, not a missing one, and that is the case this exists to repair.
        if (existing.Content is null or { Count: 0 })
        {
            existing.Content = Content();
        }
    }
}
