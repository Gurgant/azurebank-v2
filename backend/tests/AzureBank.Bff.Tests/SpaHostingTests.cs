using System.Net;
using FluentAssertions;
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

    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _build = Directory.CreateTempSubdirectory("azurebank-spa-").FullName;

    public SpaHostingTests(WebApplicationFactory<Program> factory)
    {
        File.WriteAllText(Path.Combine(_build, "index.html"), Shell);
        File.WriteAllText(Path.Combine(_build, "theme-init.js"), "/* theme */");
        Directory.CreateDirectory(Path.Combine(_build, "assets"));
        File.WriteAllText(Path.Combine(_build, "assets", "index-Cemq5B69.js"), "/* bundle */");

        _factory = factory.WithWebHostBuilder(builder => builder.UseSetting("Spa:RootPath", _build));
    }

    public void Dispose() => Directory.Delete(_build, recursive: true);

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
