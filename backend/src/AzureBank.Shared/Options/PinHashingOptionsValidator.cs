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

        // The binder loses a previous pepper in silence in four ways: it drops an entry whose key
        // it cannot convert to an int, it drops one whose key holds a section instead of a value,
        // under a key that holds a value and a section it reads the value alone, and of two keys
        // that name the same id ("1" and "01") it keeps one pepper. It also accepts
        // surrounding whitespace. So the original keys are read here too, never their secret
        // values: each must be a whole number, the only key for its id, and an entry of the bound
        // map. Signed integer keys and leading zeroes keep working. The binder converts a key with
        // the host's culture and this loop reads it with the invariant one. A sign this loop reads
        // and the binder does not leaves the key out of the bound map, and it is refused as not
        // read; a sign only the binder reads fails the whole-number rule here. For a zero or
        // negative id the binder did read, the bound-map check below retains the existing refusal.
        var previousPeppers = configuration.GetSection(PinHashingOptions.SectionName)
            .GetSection(nameof(PinHashingOptions.PreviousPinPeppers));

        // A value on the section itself (Security__PreviousPinPeppers=<pepper>) has no key to
        // judge below, and the binder reads nothing from it. The value is never printed.
        if (!string.IsNullOrEmpty(previousPeppers.Value))
        {
            errors.Add("Security:PreviousPinPeppers holds a value of its own: " +
                       "each previous pepper goes under its key id.");
        }

        var firstKeyOfId = new Dictionary<int, string>();
        foreach (var entry in previousPeppers.GetChildren())
        {
            if (!int.TryParse(entry.Key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id))
            {
                var key = Shown(entry.Key, firstPartOfAPath: entry.GetChildren().Any());
                errors.Add($"Security:PreviousPinPeppers key {key} must be a whole number >= 1.");
            }
            else if (!firstKeyOfId.TryAdd(id, entry.Key))
            {
                // Which of the two the configuration lists first is the provider's business, so
                // the sentence orders them itself.
                var (first, second) = string.CompareOrdinal(firstKeyOfId[id], entry.Key) <= 0
                    ? (firstKeyOfId[id], entry.Key)
                    : (entry.Key, firstKeyOfId[id]);
                errors.Add($"Security:PreviousPinPeppers keys {Shown(first)} and {Shown(second)} name the same id; " +
                           "only one pepper can be held under it.");
            }
            else if (entry.Value is not null && entry.GetChildren().Any())
            {
                // A value on the key and another under it: the binder reads the first and leaves
                // the second out, so the id is in the bound map and the check below sees nothing.
                errors.Add($"Security:PreviousPinPeppers key {Shown(entry.Key)} must hold exactly one value.");
            }
            else if (!options.PreviousPinPeppers.ContainsKey(id))
            {
                errors.Add($"Security:PreviousPinPeppers key {Shown(entry.Key)} was not read: " +
                           "it must be a whole number >= 1 that holds one value.");
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
                // The invariant culture, so the minus sign is the same on every host.
                errors.Add($"Security:PreviousPinPeppers key '{keyId.ToString(CultureInfo.InvariantCulture)}' must be >= 1.");
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

    // How a configuration key is named in a failure. A key as long as a pepper may be one, written
    // where its id belongs, so it is never printed: only its length is. A key is one segment of a
    // path, so of a pepper that holds ':' (or "__" in a variable's name) only the part before it
    // is the key: a key that is not a whole number and has a section under it is named by its
    // length too, however short. A whole number is what an id looks like, and is quoted. Any
    // other short key is quoted, with each control character shown as '?', so a line feed in it
    // cannot split the failure.
    private static string Shown(string key, bool firstPartOfAPath = false) =>
        key.Length >= MinPepperLength || firstPartOfAPath
            ? $"of {key.Length.ToString(CultureInfo.InvariantCulture)} characters"
            : $"'{string.Concat(key.Select(c => char.IsControl(c) ? '?' : c))}'";
}
