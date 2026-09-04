namespace AzureBank.Infrastructure.Notices;

/// <summary>
/// Refuses a pickup directory that sits inside, or points into, a git working tree.
/// </summary>
/// <remarks>
/// <para>
/// The mechanical half of "never commit a spool of addresses"; the runbook's delete-after sentence
/// is the other half. Shared by the operator verb and the in-process relay's option validation
/// (ADR-0048), so a directory the verb would refuse is one the API refuses to start with. A
/// <c>.git</c> FILE counts too — that is what a worktree carries.
/// </para>
/// <para>
/// LINKS ARE FOLLOWED. A symbolic link or a junction sitting outside every repository can point into
/// one, and the files would land where it points; on Windows git treats a junction as an ordinary
/// directory and stages what is inside it. So the walk is done twice: over the path as typed, and
/// over its physical target once every link on it is resolved. Either being inside a working tree
/// refuses the directory.
/// </para>
/// </remarks>
public static class PickupDirectoryGuard
{
    /// <summary>True when the directory, or any directory above it, is a git working tree — where it
    /// SITS and where it POINTS.</summary>
    public static bool InsideAGitRepository(string fullDirectory)
    {
        if (AncestorHoldsGit(fullDirectory))
        {
            return true;
        }

        var physical = PhysicalPath(fullDirectory);
        return !string.Equals(physical, fullDirectory, StringComparison.OrdinalIgnoreCase)
               && AncestorHoldsGit(physical);
    }

    private static bool AncestorHoldsGit(string fullDirectory)
    {
        for (var current = new DirectoryInfo(fullDirectory); current is not null; current = current.Parent)
        {
            var marker = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The directory with every link on its path resolved to what it points at.</summary>
    /// <remarks>
    /// Walks upward to the deepest link, replaces that segment by its final target, and repeats on
    /// the result until no segment is a link. Bounded, so a link loop ends the walk instead of the
    /// process; an unreadable segment is left as it is rather than guessed at.
    /// </remarks>
    public static string PhysicalPath(string fullDirectory)
    {
        var path = fullDirectory;
        for (var hops = 0; hops < 32; hops++)
        {
            string? resolved = null;
            for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            {
                FileSystemInfo? target;
                try
                {
                    target = current.Exists ? current.ResolveLinkTarget(returnFinalTarget: true) : null;
                }
                catch (IOException)
                {
                    target = null;
                }

                if (target is null)
                {
                    continue;
                }

                var relative = Path.GetRelativePath(current.FullName, path);
                resolved = relative == "." ? target.FullName : Path.Combine(target.FullName, relative);
                break;
            }

            if (resolved is null)
            {
                return path;
            }

            path = Path.GetFullPath(resolved);
        }

        return path;
    }
}
