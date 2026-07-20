using Microsoft.Extensions.Compliance.Classification;

namespace AzureBank.Api.Observability;

/// <summary>
/// AzureBank's data-classification taxonomy for the .NET compliance stack.
/// A <see cref="DataClassification"/> is a machine-readable label ("this value is PII")
/// that the redaction infrastructure resolves to a concrete <c>Redactor</c> — call sites
/// say WHAT a value is, the observability layer decides HOW it is masked.
/// </summary>
public static class DataClassifications
{
    /// <summary>
    /// Taxonomy name. Namespacing the classification (rather than reusing a framework
    /// taxonomy) keeps our labels unambiguous if a dependency ever ships its own.
    /// </summary>
    public const string TaxonomyName = "AzureBank";

    /// <summary>
    /// Personally identifiable information (e.g. email addresses). Logs are exported
    /// over OTLP to Loki, so anything carrying this label must be redacted before it
    /// reaches the logging pipeline. AzureTag handles are public by design and are
    /// deliberately NOT classified as PII.
    /// </summary>
    public static DataClassification Pii { get; } = new(TaxonomyName, "PII");
}

/// <summary>
/// Annotation form of <see cref="DataClassifications.Pii"/> for DTO properties and
/// logging-method parameters — the hook the source-generated <c>[LoggerMessage]</c>
/// redaction path would use if the logging pipeline is ever converted to it.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class PiiAttribute : DataClassificationAttribute
{
    public PiiAttribute() : base(DataClassifications.Pii)
    {
    }
}
