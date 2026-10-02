using Microsoft.Extensions.Options;

namespace AzureBank.Shared.Options;

/// <summary>
/// The public demo's settings. Binds to the "Demo" section; nothing in a committed
/// <c>appsettings.json</c> sets it, so the demo is off unless a deployment turns it on.
/// </summary>
/// <remarks>
/// The defaults live here, in code: they are what a deployment gets when it sets only
/// <c>Demo__Enabled=true</c>. <see cref="DemoOptionsValidator"/> holds the range of each number.
/// </remarks>
public class DemoOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Demo";

    /// <summary>Whether this deployment is the public demo. Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>Hours a claimed copy can be signed in to. 24.</summary>
    public int CopyLifetimeHours { get; set; } = 24;

    /// <summary>The pool of free copies.</summary>
    public DemoPoolOptions Pool { get; set; } = new();

    /// <summary>The caps on claiming a copy.</summary>
    public DemoClaimOptions Claim { get; set; } = new();

    /// <summary>The caps on one copy.</summary>
    public DemoCopyOptions Copy { get; set; } = new();

    /// <summary>The key the client address is hashed with. None by default.</summary>
    /// <remarks>
    /// <see cref="DemoOptionsValidator"/> has no rule for it: a host that does not hash addresses
    /// must start without it. A host that hashes them has to require it itself.
    /// </remarks>
    public string? ClientKeySecret { get; set; }
}

/// <summary>The "Demo:Pool" section.</summary>
public class DemoPoolOptions
{
    /// <summary>Free copies the pool is topped up to. 50.</summary>
    public int TargetFree { get; set; } = 50;

    /// <summary>Free copies below which a run reports the pool as low. 20.</summary>
    public int LowMark { get; set; } = 20;

    /// <summary>Hours after which a free copy is rebuilt. 44.</summary>
    /// <remarks>
    /// A copy's ledger is dated as offsets from the instant it was seeded, so a free copy that sat
    /// for long would hand a visitor a history that ends days ago.
    /// </remarks>
    public int MaxFreeAgeHours { get; set; } = 44;

    /// <summary>Claims in a rolling 24 hours past which the pool is no longer topped up. 150.</summary>
    public int MaxClaimsPerDay { get; set; } = 150;
}

/// <summary>The "Demo:Claim" section.</summary>
public class DemoClaimOptions
{
    /// <summary>Claims one client may make in a rolling 24 hours. 10.</summary>
    public int MaxPerClientPerDay { get; set; } = 10;
}

/// <summary>The "Demo:Copy" section.</summary>
public class DemoCopyOptions
{
    /// <summary>Authenticated unsafe requests one copy may make. 200.</summary>
    public int MaxWrites { get; set; } = 200;
}

/// <summary>Validates <see cref="DemoOptions"/>. Every host that reads the section registers it.</summary>
/// <remarks>
/// <para>
/// The ranges are checked whether or not the demo is on, so a value out of range is refused on the
/// day it is written and not on the day somebody turns the demo on.
/// </para>
/// <para>
/// Every message names the setting by its configuration key, which is what an operator searches for.
/// </para>
/// </remarks>
public sealed class DemoOptionsValidator : IValidateOptions<DemoOptions>
{
    private const string PoolSection = "Demo:Pool";
    private const string ClaimSection = "Demo:Claim";
    private const string CopySection = "Demo:Copy";
    private const string CopyLifetimeHours = "Demo:CopyLifetimeHours";
    private const string TargetFree = "Demo:Pool:TargetFree";
    private const string LowMark = "Demo:Pool:LowMark";
    private const string MaxFreeAgeHours = "Demo:Pool:MaxFreeAgeHours";
    private const string MaxClaimsPerDay = "Demo:Pool:MaxClaimsPerDay";
    private const string MaxPerClientPerDay = "Demo:Claim:MaxPerClientPerDay";
    private const string MaxWrites = "Demo:Copy:MaxWrites";

    /// <summary>The most claims a day <c>Demo:Pool:MaxClaimsPerDay</c> may allow.</summary>
    private const int MaxClaimsPerDayCeiling = 10_000;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, DemoOptions options)
    {
        // Each is created by default, and binding leaves it in place. Code that nulled one would
        // otherwise fail below with a NullReferenceException that names no setting. The refusal
        // names each section that is null, and no section that is not.
        var missing = new List<string>();

        void Required(string section, object? value)
        {
            if (value is null)
            {
                missing.Add($"{section} must not be null.");
            }
        }

        Required(PoolSection, options.Pool);
        Required(ClaimSection, options.Claim);
        Required(CopySection, options.Copy);

        if (missing.Count > 0)
        {
            return ValidateOptionsResult.Fail(missing);
        }

        var errors = new List<string>();

        void Range(string key, int value, int min, int max)
        {
            if (value < min || value > max)
            {
                errors.Add($"{key} must be between {min} and {max}, and is {value}.");
            }
        }

        Range(CopyLifetimeHours, options.CopyLifetimeHours, 1, 168);
        Range(TargetFree, options.Pool.TargetFree, 1, 500);
        Range(MaxFreeAgeHours, options.Pool.MaxFreeAgeHours, 1, 720);
        Range(MaxPerClientPerDay, options.Claim.MaxPerClientPerDay, 1, 1000);
        Range(MaxWrites, options.Copy.MaxWrites, 10, 100_000);

        // A run tops the pool up to its target and no further, so a mark above the target would
        // report the pool as low at every run.
        if (options.Pool.LowMark < 0 || options.Pool.LowMark > options.Pool.TargetFree)
        {
            errors.Add(
                $"{LowMark} must be between 0 and {TargetFree} ({options.Pool.TargetFree}), "
                + $"and is {options.Pool.LowMark}.");
        }

        // A ceiling under the target would keep the pool below its target even on a day without a claim.
        if (options.Pool.MaxClaimsPerDay < options.Pool.TargetFree || options.Pool.MaxClaimsPerDay > MaxClaimsPerDayCeiling)
        {
            errors.Add(
                $"{MaxClaimsPerDay} must be between {TargetFree} ({options.Pool.TargetFree}) and "
                + $"{MaxClaimsPerDayCeiling}, and is {options.Pool.MaxClaimsPerDay}.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
