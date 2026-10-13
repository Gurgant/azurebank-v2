using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using AzureBank.Api.Transformers;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace AzureBank.Tests.Unit.Transformers;

/// <summary>
/// The standard validation attributes that reach this transformer on no type of the API today, as
/// the published schema would show them: <c>[MinLength]</c>, <c>[Range]</c>, <c>[Phone]</c>,
/// <c>[Url]</c> and <c>[CreditCard]</c>.
/// </summary>
/// <remarks>
/// <para>
/// The committed contract holds the attributes the request bodies do carry: a change to one of
/// those arms changes <c>docs/api/openapiv1.json</c> and the contract's gate fails. An arm no type
/// reaches is held by nothing, and the day a request body takes <c>[Range(1, 50)]</c> its bounds
/// are published by a line that never ran. (The query of <c>GET /api/transactions</c> carries
/// <c>[Range]</c> today, and its bounds reach the contract without passing through this arm.) So
/// each arm is run here on a type of this file's own, and read from the schema it leaves.
/// </para>
/// <para>
/// The transformer is called as the framework calls it for a complex type: the schema with one
/// entry per property, named in camel case, and the type's own <c>JsonTypeInfo</c>.
/// </para>
/// </remarks>
public sealed class DataAnnotationSchemaTransformerTests
{
    private sealed class Annotated
    {
        [MinLength(3)]
        public string? Nickname { get; set; }

        [Range(1, 50)]
        public int PageSize { get; set; }

        [Range(0.5, 99.75)]
        public double Rate { get; set; }

        [Phone]
        public string? Mobile { get; set; }

        [Url]
        public string? Website { get; set; }

        [CreditCard]
        public string? Card { get; set; }

        public string? Plain { get; set; }
    }

    private static async Task<Dictionary<string, OpenApiSchema>> Transformed()
    {
        var properties = typeof(Annotated).GetProperties()
            .ToDictionary(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name), _ => new OpenApiSchema());
        var schema = new OpenApiSchema
        {
            Properties = properties.ToDictionary(pair => pair.Key, pair => (IOpenApiSchema)pair.Value),
        };
        using var services = new ServiceCollection().BuildServiceProvider();
        var context = new OpenApiSchemaTransformerContext
        {
            DocumentName = "v1",
            ParameterDescription = null,
            JsonTypeInfo = JsonSerializerOptions.Web.GetTypeInfo(typeof(Annotated)),
            JsonPropertyInfo = null,
            ApplicationServices = services,
        };

        await new DataAnnotationSchemaTransformer().TransformAsync(schema, context, CancellationToken.None);

        return properties;
    }

    [Fact]
    public async Task MinLength_PublishesTheShortestLength_AndNoLongest()
    {
        var schema = (await Transformed())["nickname"];

        using var all = new AssertionScope();
        schema.MinLength.Should().Be(3);
        schema.MaxLength.Should().BeNull("[MinLength] says nothing about the longest value");
    }

    [Fact]
    public async Task Range_PublishesBothBounds_AsInvariantNumbers()
    {
        // Under a culture that writes a comma: under one that writes a point, a conversion that
        // follows the current culture publishes "0.5" too, and the test could not tell the two.
        var machine = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("it-IT");
        try
        {
            var schemas = await Transformed();

            using var all = new AssertionScope();
            schemas["pageSize"].Minimum.Should().Be("1");
            schemas["pageSize"].Maximum.Should().Be("50");

            // A point, whatever the culture: a comma here is not a JSON Schema number.
            schemas["rate"].Minimum.Should().Be("0.5");
            schemas["rate"].Maximum.Should().Be("99.75");
        }
        finally
        {
            CultureInfo.CurrentCulture = machine;
        }
    }

    [Theory]
    [InlineData("mobile", "phone")]
    [InlineData("website", "uri")]
    [InlineData("card", "credit-card")]
    public async Task AFormatAttribute_PublishesItsFormat(string property, string format)
    {
        var schema = (await Transformed())[property];

        schema.Format.Should().Be(format);
    }

    [Fact]
    public async Task APropertyWithNoAttribute_IsLeftAsItWas()
    {
        // CONTROL: what the tests above read was written for the attribute, not for every property.
        var schema = (await Transformed())["plain"];

        using var all = new AssertionScope();
        schema.MinLength.Should().BeNull();
        schema.MaxLength.Should().BeNull();
        schema.Minimum.Should().BeNull();
        schema.Maximum.Should().BeNull();
        schema.Format.Should().BeNull();
    }
}
