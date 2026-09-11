namespace AzureBank.Bff.Options;

/// <summary>
/// Where the built SPA lives when this BFF serves it (ADR-0054). Bound from <c>Spa</c>.
/// </summary>
/// <remarks>
/// Unset, the default, means the BFF serves no pages at all: the development loop, where Vite serves
/// the SPA and proxies <c>/api</c> and <c>/bff</c> here. Set, it must name a directory holding an
/// <c>index.html</c>, and <see cref="SpaOptionsValidator"/> stops the host at startup when it does
/// not — a BFF that was told to serve the app and silently serves 404s is the failure to prevent.
/// </remarks>
public class SpaOptions
{
    public const string SectionName = "Spa";

    /// <summary>The Vite build directory (<c>frontend/dist</c>): absolute, or relative to the content root.</summary>
    public string? RootPath { get; set; }

    /// <summary>The configured directory as an absolute path, or null when the SPA is not served.</summary>
    public string? ResolveRoot(string contentRootPath) =>
        string.IsNullOrWhiteSpace(RootPath)
            ? null
            : Path.GetFullPath(Path.IsPathRooted(RootPath) ? RootPath : Path.Combine(contentRootPath, RootPath));
}
