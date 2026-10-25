using System.Reflection;
using System.Text.Json;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Where the repository's own files are, for the tests that read them: the sources, the documents
/// and the committed contract.
/// </summary>
/// <remarks>
/// A guard that reads nothing reports clean for ever. So each member fails the test, with one
/// sentence, when what it looks for is not there, and never hands back a path that leads nowhere.
/// </remarks>
internal static class RepositoryFiles
{
    /// <summary>
    /// The first folder above the test assembly that holds <c>.github</c>: the root of the checkout
    /// the tests were built in.
    /// </summary>
    public static DirectoryInfo Root()
    {
        var start = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var dir = new DirectoryInfo(start);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            Assert.Fail(
                $"No folder from {start} up holds .github, and a test that reads the repository's files "
                + "must fail, never pass, when it cannot find them.");
        }

        return dir;
    }

    /// <summary>
    /// The <c>backend</c> folder under <see cref="Root"/>, which holds the solution file. The test
    /// fails when the solution file is not there.
    /// </summary>
    public static DirectoryInfo Backend() => new(Path.GetDirectoryName(ExistingFile("backend", "AzureBank.slnx"))!);

    /// <summary>
    /// The path of a file under <see cref="Root"/>, named by its folders and its file name. The test
    /// fails when the file is not there.
    /// </summary>
    public static string ExistingFile(params string[] parts)
    {
        var path = Path.Combine(Root().FullName, Path.Combine(parts));
        if (!File.Exists(path))
        {
            Assert.Fail($"{path} does not exist, and a test that reads it must fail, never pass, without it.");
        }

        return path;
    }

    /// <summary>
    /// The committed contract, <c>docs/api/openapiv1.json</c>, parsed. The element is a copy, so it
    /// outlives the parsed document.
    /// </summary>
    public static JsonElement PublishedContract()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ExistingFile("docs", "api", "openapiv1.json")));
        return document.RootElement.Clone();
    }
}
