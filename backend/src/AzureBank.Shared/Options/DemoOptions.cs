using Microsoft.Extensions.Options;

namespace AzureBank.Shared.Options;

/// <summary>
/// The public demo's settings. Binds to the "Demo" section; nothing in a committed
/// <c>appsettings.json</c> sets it, so the demo is off unless a deployment turns it on.
/// </summary>
/// <remarks>
/// NOT FILLED IN YET. The properties exist so the tests of the pool compile; the defaults and the
/// rules <see cref="DemoOptionsValidator"/> applies arrive with the options themselves.
/// </remarks>
public class DemoOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Demo";

    /// <summary>Whether this deployment is the public demo.</summary>
    public bool Enabled { get; set; }

    /// <summary>Hours a claimed copy can be signed in to.</summary>
    public int CopyLifetimeHours { get; set; }

    /// <summary>The pool of free copies.</summary>
    public DemoPoolOptions Pool { get; set; } = new();

    /// <summary>The caps on claiming a copy.</summary>
    public DemoClaimOptions Claim { get; set; } = new();

    /// <summary>The caps on one copy.</summary>
    public DemoCopyOptions Copy { get; set; } = new();

    /// <summary>The key the client address is hashed with.</summary>
    public string? ClientKeySecret { get; set; }
}

/// <summary>The "Demo:Pool" section.</summary>
public class DemoPoolOptions
{
    /// <summary>Free copies the pool is topped up to.</summary>
    public int TargetFree { get; set; }

    /// <summary>Free copies below which a run reports the pool as low.</summary>
    public int LowMark { get; set; }

    /// <summary>Hours after which a free copy is rebuilt.</summary>
    public int MaxFreeAgeHours { get; set; }

    /// <summary>Claims in a rolling 24 hours past which the pool is no longer topped up.</summary>
    public int MaxClaimsPerDay { get; set; }
}

/// <summary>The "Demo:Claim" section.</summary>
public class DemoClaimOptions
{
    /// <summary>Claims one client may make in a rolling 24 hours.</summary>
    public int MaxPerClientPerDay { get; set; }
}

/// <summary>The "Demo:Copy" section.</summary>
public class DemoCopyOptions
{
    /// <summary>Authenticated unsafe requests one copy may make.</summary>
    public int MaxWrites { get; set; }
}

/// <summary>Validates <see cref="DemoOptions"/>. Every host that reads the section registers it.</summary>
public sealed class DemoOptionsValidator : IValidateOptions<DemoOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, DemoOptions options) => ValidateOptionsResult.Success;
}
