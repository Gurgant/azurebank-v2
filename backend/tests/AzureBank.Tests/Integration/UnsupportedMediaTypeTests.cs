using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AzureBank.Tests.Fixtures;
using FluentAssertions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Which request media types a body-taking operation accepts, and which it refuses with 415.
/// </summary>
/// <remarks>
/// The published 415 description names the accepted set, and the document lists the same three
/// types on every request body: <c>application/json</c>, <c>text/json</c> and
/// <c>application/*+json</c>, the default JSON input formatter's. Its first wording said "the
/// request body is not application/json", which a client generated from the document would read as
/// refusing <c>text/json</c> (found in review, 2026-09-15). This pins the set on the anonymous login,
/// where an accepted body with wrong credentials answers 401 and a refused one answers 415 before
/// model binding.
/// </remarks>
public class UnsupportedMediaTypeTests : IntegrationTestBase
{
    public UnsupportedMediaTypeTests(CustomWebApplicationFactory factory) : base(factory) { }

    private const string Body = "{\"email\":\"nobody@example.com\",\"password\":\"WrongPass123!\"}";

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/json")]
    [InlineData("application/problem+json")]
    public async Task AJsonMediaTypeTheFormatterAccepts_IsBound_NotRefused(string mediaType)
    {
        using var content = new StringContent(Body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);

        var response = await Client.PostAsync("/api/auth/login", content);

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "{0} matches application/json, text/json or application/*+json, so the body is bound and the "
            + "wrong credentials are what refuse it", mediaType);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/xml")]
    public async Task AnyOtherMediaType_Is415_BeforeModelBinding(string mediaType)
    {
        using var content = new StringContent(Body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);

        var response = await Client.PostAsync("/api/auth/login", content);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }
}
