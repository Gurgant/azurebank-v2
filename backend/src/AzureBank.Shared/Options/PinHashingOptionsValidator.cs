using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AzureBank.Shared.Options;

/// <summary>
/// Validates the PIN-pepper keyring (ADR-0011). One shared validator used by BOTH
/// the API and the Seeder so their rules cannot drift, and so the Seeder can be made
/// to fail fast on the same conditions the API rejects.
/// </summary>
public sealed class PinHashingOptionsValidator(IConfiguration configuration) : IValidateOptions<PinHashingOptions>
{
    /// <summary>Minimum pepper length (characters). A pepper below this is rejected.</summary>
    public const int MinPepperLength = 32;

    public ValidateOptionsResult Validate(string? name, PinHashingOptions options)
    {
        // PreviousPinPeppers is initialized to an empty map by default, but guard
        // against a caller nulling it — the checks below would otherwise NRE.
        if (options.PreviousPinPeppers is null)
        {
            return ValidateOptionsResult.Fail("Security:PreviousPinPeppers must not be null.");
        }

        var errors = new List<string>();

        // The binder silently drops a dictionary entry whose key cannot become an int, and
        // accepts surrounding whitespace. Read the original keys too, never their secret values.
        // Keep signed integer keys and leading zeroes working; the bound-map check below retains
        // the existing refusal for zero and negative ids.
        var previousPeppers = configuration.GetSection(PinHashingOptions.SectionName)
            .GetSection(nameof(PinHashingOptions.PreviousPinPeppers));
        foreach (var entry in previousPeppers.GetChildren())
        {
            if (!int.TryParse(entry.Key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
            {
                errors.Add($"Security:PreviousPinPeppers key '{entry.Key}' must be a whole number >= 1.");
            }
        }

        // Active pepper.
        if (string.IsNullOrWhiteSpace(options.PinPepper) || options.PinPepper.Length < MinPepperLength)
        {
            errors.Add($"Security:PinPepper must be configured with at least {MinPepperLength} characters " +
                       "(a server-side secret kept OUT of the database; user-secrets in dev, Key Vault in prod).");
        }
        if (options.PinPepperKeyId < 1)
        {
            errors.Add("Security:PinPepperKeyId must be >= 1.");
        }

        // Retired peppers (the rest of the keyring).
        foreach (var (keyId, pepper) in options.PreviousPinPeppers)
        {
            if (keyId < 1)
            {
                errors.Add($"Security:PreviousPinPeppers key '{keyId}' must be >= 1.");
            }
            if (string.IsNullOrWhiteSpace(pepper) || pepper.Length < MinPepperLength)
            {
                errors.Add($"Security:PreviousPinPeppers[{keyId}] must be at least {MinPepperLength} characters.");
            }
        }

        // The active key id must not also live in the retired map (ambiguous resolution).
        if (options.PreviousPinPeppers.ContainsKey(options.PinPepperKeyId))
        {
            errors.Add($"Security:PreviousPinPeppers must not contain the active PinPepperKeyId ({options.PinPepperKeyId}).");
        }

        // Every pepper value must be distinct — a "rotation" that reuses an old secret
        // is a silent no-op and almost always a config mistake.
        var values = new List<string> { options.PinPepper ?? string.Empty };
        values.AddRange(options.PreviousPinPeppers.Values);
        var nonEmpty = values.Where(v => !string.IsNullOrEmpty(v)).ToList();
        if (nonEmpty.Count != nonEmpty.Distinct(StringComparer.Ordinal).Count())
        {
            errors.Add("Security pepper values (active + previous) must all be distinct.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
