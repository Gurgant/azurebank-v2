using System.Net;
using System.Text;
using AzureBank.Bff.Extensions;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AzureBank.Bff.Tests;

/// <summary>
/// The BFF serving the built SPA (ADR-0054), against a stand-in build in a temporary directory.
/// </summary>
/// <remarks>
/// Every status below was first observed on the running BFF serving the real <c>frontend/dist</c>
/// (2026-09-11T14:41Z, <c>--Spa:RootPath=../../../frontend/dist</c>): the shell for <c>/</c>,
/// <c>/settings</c> and <c>/accounts/123</c>, 200 with <c>no-cache</c>; a fingerprinted asset with
/// <c>public, max-age=31536000, immutable</c>; 404 for a missing file and for unknown
/// <c>/bff</c> and <c>/health</c> paths; 405 for <c>GET /bff/auth/login</c>, as on main; 401 for
/// an anonymous <c>/api</c> call. The server's paths are the point: with <c>MapFallbackToFile</c>
/// in place of this middleware, <c>GET /bff/auth/login</c>, <c>/bff/nope</c> and
/// <c>/health/nope</c> all answered 200 with the page.
/// </remarks>
public sealed class SpaHostingTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Shell = "<!doctype html><title>stand-in shell</title>";

    private readonly WebApplicationFactory<Program> _root;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _build = Directory.CreateTempSubdirectory("azurebank-spa-").FullName;

    public SpaHostingTests(WebApplicationFactory<Program> factory)
    {
        _root = factory;
        File.WriteAllText(Path.Combine(_build, "index.html"), Shell);
        File.WriteAllText(Path.Combine(_build, "theme-init.js"), "/* theme */");
        Directory.CreateDirectory(Path.Combine(_build, "assets"));
        File.WriteAllText(Path.Combine(_build, "assets", "index-Cemq5B69.js"), "/* bundle */");

        _factory = factory.WithWebHostBuilder(builder => builder.UseSetting("Spa:RootPath", _build));
    }

    public void Dispose()
    {
        Directory.Delete(_build, recursive: true);
        foreach (var build in _moreBuilds)
        {
            Directory.Delete(build, recursive: true);
        }
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/settings")]
    [InlineData("/accounts/123")]
    public async Task ANavigation_GetsTheShell_NeverCached_AndUnderTheCsp(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        (await response.Content.ReadAsStringAsync()).Should().Be(Shell);
        response.Headers.CacheControl!.NoCache.Should().BeTrue(
            "the shell names this build's fingerprinted bundle and must not outlive a deploy");
        SecurityHeadersTests.AssertTheWholeSet(response);
    }

    [Fact]
    public async Task AFingerprintedAsset_IsCachedForGood_AndAnUnhashedFileIsRevalidated()
    {
        var client = _factory.CreateClient();

        var asset = await client.GetAsync("/assets/index-Cemq5B69.js");
        asset.StatusCode.Should().Be(HttpStatusCode.OK);
        asset.Headers.CacheControl!.ToString().Should().Be("public, max-age=31536000, immutable");
        SecurityHeadersTests.AssertTheWholeSet(asset);

        var themeScript = await client.GetAsync("/theme-init.js");
        themeScript.StatusCode.Should().Be(HttpStatusCode.OK);
        themeScript.Headers.CacheControl!.NoCache.Should().BeTrue();
    }

    [Theory]
    [InlineData("GET", "/bff/nope", HttpStatusCode.NotFound)]
    [InlineData("GET", "/health/nope", HttpStatusCode.NotFound)]
    [InlineData("GET", "/bff/auth/login", HttpStatusCode.MethodNotAllowed)]
    [InlineData("DELETE", "/bff/auth/me", HttpStatusCode.MethodNotAllowed)]
    [InlineData("GET", "/api/accounts", HttpStatusCode.Unauthorized)]
    [InlineData("POST", "/settings", HttpStatusCode.NotFound)]
    [InlineData("GET", "/assets/missing.js", HttpStatusCode.NotFound)]
    [InlineData("GET", "/missing.js", HttpStatusCode.NotFound)]
    public async Task WhatIsNotANavigation_NeverGetsTheShell(string method, string path, HttpStatusCode expected)
    {
        var response = await _factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        response.StatusCode.Should().Be(expected);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("stand-in shell");
    }

    [Fact]
    public async Task WithoutARootPath_NoPageIsServed()
    {
        // The development loop: Vite serves the SPA and the BFF serves none, exactly as on main.
        using var factory = new WebApplicationFactory<Program>();
        var response = await factory.CreateClient().GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void ARootPathWithNoIndex_StopsTheHost()
    {
        var empty = Directory.CreateTempSubdirectory("azurebank-spa-empty-").FullName;
        try
        {
            using var root = new WebApplicationFactory<Program>();
            var factory = root.WithWebHostBuilder(builder => builder.UseSetting("Spa:RootPath", empty));

            var exception = Record.Exception(() => factory.CreateClient());

            // Program.cs's top-level catch logs the validation failure and ends the host, so what
            // reaches the factory is only that no host was built; SpaOptionsValidatorTests pin WHY.
            exception.Should().NotBeNull(
                "a BFF told to serve the app from a directory with no index.html would otherwise " +
                "start healthy and answer every page with 404");
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    // ── The demo's tag (Demo:Enabled) ────────────────────────────────────────────────────────────

    /// <summary>
    /// A stand-in shell with a head, as the built one has, and a character outside ASCII: the page
    /// is compared as bytes.
    /// </summary>
    private const string ShellWithAHead =
        "<!doctype html><html lang=\"it\"><head><meta charset=\"UTF-8\"><title>stand-in shell – caffè</title></head>"
        + "<body><div id=\"root\"></div></body></html>";

    /// <summary>The same shell with the demo's tag where the BFF puts it: right before the head closes.</summary>
    private static readonly string TaggedShell = ShellWithAHead.Replace(
        "</head>", SpaHostingExtensions.DemoTag + "</head>", StringComparison.Ordinal);

    private readonly List<string> _moreBuilds = [];

    /// <summary>A host serving a build whose <c>index.html</c> is <paramref name="shell"/>, with the demo on or off.</summary>
    private WebApplicationFactory<Program> HostServing(string shell, bool demo)
    {
        var build = Directory.CreateTempSubdirectory("azurebank-spa-demo-").FullName;
        _moreBuilds.Add(build);
        File.WriteAllBytes(Path.Combine(build, "index.html"), Encoding.UTF8.GetBytes(shell));
        File.WriteAllText(Path.Combine(build, "theme-init.js"), "/* theme */");

        return _root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spa:RootPath", build);
            if (demo)
            {
                builder.UseSetting("Demo:Enabled", "true");
            }
        });
    }

    private static int CountOf(string text, string part) => text.Split(part).Length - 1;

    [Theory]
    [InlineData("/")]
    [InlineData("/settings")]
    [InlineData("/accounts/123")]
    [InlineData("/index.html")]
    // Another spelling of the file's name. Where the file system ignores case the static files
    // answer it with the file, which has no tag; where it does not, nothing answers it.
    [InlineData("/INDEX.HTML")]
    public async Task WithTheDemoOn_TheShellAndIndexHtml_CarryTheTagExactlyOnce_BeforeTheHeadCloses(string path)
    {
        using var host = HostServing(ShellWithAHead, demo: true);

        var response = await host.CreateClient().GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var page = Encoding.UTF8.GetString(bytes);
        using (new AssertionScope())
        {
            CountOf(page, SpaHostingExtensions.DemoTag).Should().Be(1, "the page says it is the demo, once");
            page.IndexOf(SpaHostingExtensions.DemoTag, StringComparison.Ordinal).Should().BePositive()
                .And.BeLessThan(page.IndexOf("</head>", StringComparison.Ordinal), "a meta tag belongs to the head");

            // Today's shell and the tag, and nothing else: byte for byte.
            bytes.Should().Equal(Encoding.UTF8.GetBytes(TaggedShell));
            page.Replace(SpaHostingExtensions.DemoTag, string.Empty, StringComparison.Ordinal).Should().Be(ShellWithAHead);
        }
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    public async Task WithTheDemoOn_TheTaggedPage_KeepsItsContentType_NoCache_AndTheSecurityHeaders(string path)
    {
        using var host = HostServing(ShellWithAHead, demo: true);

        var response = await host.CreateClient().GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain(
            SpaHostingExtensions.DemoTag, "ARRANGE: this is the tagged page");
        using (new AssertionScope())
        {
            (response.Content.Headers.ContentType?.MediaType).Should().Be("text/html");
            (response.Content.Headers.ContentType?.CharSet).Should().Be("utf-8");
            (response.Content.Headers.ContentLength).Should().Be(Encoding.UTF8.GetByteCount(TaggedShell));
            (response.Headers.CacheControl?.NoCache).Should().BeTrue(
                "the shell names this build's fingerprinted bundle and must not outlive a deploy");
            SecurityHeadersTests.AssertTheWholeSet(response);
        }
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    public async Task WithTheDemoOn_AHeadOfThePage_AnswersTheTaggedPagesHeaders_AndNoBody(string path)
    {
        using var host = HostServing(ShellWithAHead, demo: true);

        var response = await host.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Head, path));

        using (new AssertionScope())
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (response.Content.Headers.ContentType?.MediaType).Should().Be("text/html");
            (response.Content.Headers.ContentType?.CharSet).Should().Be("utf-8");
            (response.Content.Headers.ContentLength).Should().Be(
                Encoding.UTF8.GetByteCount(TaggedShell), "a HEAD names the length a GET would send: the tagged page's");
            (response.Headers.CacheControl?.NoCache).Should().BeTrue();
            (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        }
    }

    // CONTROL: green before this change. With the demo off the page is the file, whatever is in it.
    [Theory]
    [InlineData("/")]
    [InlineData("/settings")]
    [InlineData("/index.html")]
    public async Task WithTheDemoOff_ThePageAndIndexHtml_AreTheFilesOwnBytes(string path)
    {
        using var host = HostServing(ShellWithAHead, demo: false);

        var response = await host.CreateClient().GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using (new AssertionScope())
        {
            bytes.Should().Equal(Encoding.UTF8.GetBytes(ShellWithAHead));
            Encoding.UTF8.GetString(bytes).Should().NotContain("azurebank-demo");
            (response.Content.Headers.ContentType?.MediaType).Should().Be("text/html");
        }
    }

    [Fact]
    public async Task WithTheDemoOn_AShellWithNoHead_StopsTheHost()
    {
        // A page with no tag reads as "the demo is off" and nothing in the log would say why: a
        // shell the tag cannot be put in stops the host, as a missing index.html does.
        using var headless = HostServing(Shell, demo: true);

        var exception = Record.Exception(() => headless.CreateClient());

        exception.Should().NotBeNull("a demo that cannot tell its page it is the demo must not start");

        // Any failure to start would satisfy the line above. So the same shell with the demo off,
        // and a shell with a head with the demo on: both start, and what stopped the first one is
        // the two together. (SpaDemoTagTests holds what the refusal says.)
        using var sameShellDemoOff = HostServing(Shell, demo: false);
        (await sameShellDemoOff.CreateClient().GetStringAsync("/")).Should().Be(Shell);
        using var withAHead = HostServing(ShellWithAHead, demo: true);
        (await withAHead.CreateClient().GetStringAsync("/")).Should().Be(TaggedShell);
    }

    // CONTROL: green before this change. The development loop with the demo on: Vite serves the
    // page, the BFF serves none, and there is no shell to tag or to refuse.
    [Fact]
    public async Task WithTheDemoOn_AndNoRootPath_TheBffStarts_AndServesNoPage()
    {
        using var root = new WebApplicationFactory<Program>();
        var factory = root.WithWebHostBuilder(builder => builder.UseSetting("Demo:Enabled", "true"));
        var client = factory.CreateClient();

        (await client.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/index.html")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK, "CONTROL: the host started");
    }
}

/// <summary>
/// Where the demo's tag goes in a shell, and what a shell it cannot go in is answered
/// (<see cref="SpaHostingExtensions.WithDemoTag"/>).
/// </summary>
public sealed class SpaDemoTagTests
{
    private static readonly string IndexPath = Path.Combine("wwwroot", "index.html");

    private static string Tagged(string shell) =>
        Encoding.UTF8.GetString(SpaHostingExtensions.WithDemoTag(Encoding.UTF8.GetBytes(shell), IndexPath));

    [Fact]
    public void TheTag_IsTheOneThePageReads()
    {
        SpaHostingExtensions.DemoTag.Should().Be("<meta name=\"azurebank-demo\" content=\"true\">");
    }

    [Theory]
    [InlineData("<html><head><title>t</title></head><body></body></html>", "<html><head><title>t</title>{tag}</head><body></body></html>")]
    // A head end in upper case is a head end.
    [InlineData("<HTML><HEAD></HEAD><BODY></BODY></HTML>", "<HTML><HEAD>{tag}</HEAD><BODY></BODY></HTML>")]
    // The first head end only: a second one, in a script's text, is left as it is.
    [InlineData("<head></head><body><script>var s = '</head>';</script></body>", "<head>{tag}</head><body><script>var s = '</head>';</script></body>")]
    // The bytes around it are kept: here, two characters outside ASCII.
    [InlineData("<head><title>caffè – bar</title></head>", "<head><title>caffè – bar</title>{tag}</head>")]
    public void TheTag_GoesRightBeforeTheFirstHeadEnd_AndNothingElseChanges(string shell, string expected)
    {
        Tagged(shell).Should().Be(expected.Replace("{tag}", SpaHostingExtensions.DemoTag, StringComparison.Ordinal));
    }

    [Fact]
    public void AByteOrderMark_IsKeptWhereItIs()
    {
        byte[] shell = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("<head></head>")];

        var tagged = SpaHostingExtensions.WithDemoTag(shell, IndexPath);

        tagged.Should().Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes($"<head>{SpaHostingExtensions.DemoTag}</head>")]);
    }

    [Theory]
    [InlineData("<!doctype html><title>stand-in shell</title>")]
    [InlineData("")]
    [InlineData("<head>")]
    public void AShellWithNoHeadEnd_IsRefused_NamingTheFileAndTheSetting(string shell)
    {
        var act = () => SpaHostingExtensions.WithDemoTag(Encoding.UTF8.GetBytes(shell), IndexPath);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("Demo:Enabled").And.Contain(IndexPath).And.Contain("</head>");
    }
}

public sealed class SpaOptionsValidatorTests
{
    private sealed class Environment(string contentRoot) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "AzureBank.Bff";
        public string ContentRootPath { get; set; } = contentRoot;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static readonly string ContentRoot = Path.GetTempPath();

    private readonly AzureBank.Bff.Options.SpaOptionsValidator _sut = new(new Environment(ContentRoot));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoRootPath_ServesNothing_AndIsValid(string? rootPath)
    {
        _sut.Validate(null, new AzureBank.Bff.Options.SpaOptions { RootPath = rootPath })
            .Succeeded.Should().BeTrue();
    }

    [Fact]
    public void ADirectoryWithAnIndex_IsValid_RelativeToTheContentRoot()
    {
        var build = Directory.CreateTempSubdirectory("azurebank-spa-ok-");
        try
        {
            File.WriteAllText(Path.Combine(build.FullName, "index.html"), "<!doctype html>");

            _sut.Validate(null, new AzureBank.Bff.Options.SpaOptions { RootPath = build.Name })
                .Succeeded.Should().BeTrue();
        }
        finally
        {
            build.Delete(recursive: true);
        }
    }

    [Fact]
    public void ADirectoryWithoutAnIndex_FailsAndSaysWhere()
    {
        var build = Directory.CreateTempSubdirectory("azurebank-spa-none-");
        try
        {
            var result = _sut.Validate(null, new AzureBank.Bff.Options.SpaOptions { RootPath = build.FullName });

            result.Failed.Should().BeTrue();
            result.FailureMessage.Should().Contain("Spa:RootPath").And.Contain(build.FullName);
        }
        finally
        {
            build.Delete(recursive: true);
        }
    }
}
