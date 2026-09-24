using System.Globalization;
using System.Net;
using System.Text.Json;
using AzureBank.Shared.Constants;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace AzureBank.Tests.Integration;

/// <summary>
/// An EMPTY value in the query is refused the way an invalid one is, not read as absent. Measured
/// 2026-09-24 before the fix, on all seven nullable query parameters: <c>?at=</c> on the balance,
/// and <c>?FromDate=</c>, <c>?ToDate=</c> and <c>?AccountId=</c> on the transaction list and on its
/// summary, answered 200, empty and whitespace-only alike, while <c>=garbage</c> answered 400.
/// Schemathesis found them with <c>--checks all</c>, "API accepted schema-violating request":
/// three dates first, and the two <c>AccountId</c>s once the dates no longer hid them.
/// </summary>
public class EmptyQueryValueTests : IntegrationTestBase
{
    private const string Balance = "/api/accounts/{account}/balance";
    private const string List = "/api/transactions";
    private const string Summary = "/api/transactions/summary";

    public EmptyQueryValueTests(CustomWebApplicationFactory factory) : base(factory) { }

    /// <summary>
    /// Path, key, the value sent, and the error the framework writes for it. <c>at</c> is an action
    /// parameter and the others are properties of a filter object, and the framework words the two
    /// differently: the fix had to write the same wording, not a wording of its own.
    /// </summary>
    public static TheoryData<string, string, string, string> Refused => new()
    {
        { Balance, "at", "", "The value '' is not valid." },
        { Balance, "at", " ", "The value ' ' is not valid." },
        { List, "FromDate", "", "The value '' is not valid for FromDate." },
        { List, "ToDate", "", "The value '' is not valid for ToDate." },
        { List, "ToDate", " ", "The value ' ' is not valid for ToDate." },
        { Summary, "FromDate", "", "The value '' is not valid for FromDate." },
        { Summary, "ToDate", "", "The value '' is not valid for ToDate." },
        { List, "AccountId", "", "The value '' is not valid for AccountId." },
        { List, "AccountId", " ", "The value ' ' is not valid for AccountId." },
        { Summary, "AccountId", "", "The value '' is not valid for AccountId." },
        // The control: garbage was refused before the fix, in exactly this wording.
        { Summary, "ToDate", "garbage", "The value 'garbage' is not valid for ToDate." },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task AnEmptyValue_IsRefusedLikeAnInvalidOne(string path, string key, string sent, string error)
    {
        var (token, _, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        var response = await Client.GetAsync(
            $"{path.Replace("{account}", accountId.ToString())}?{key}={Uri.EscapeDataString(sent)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errors").GetProperty(key).EnumerateArray()
            .Select(e => e.GetString()).Should().Equal(error);
    }

    /// <summary>
    /// An absent value and a real one are what the fix must NOT touch, and neither is a real one
    /// with white space around it: the framework trims a GUID before parsing it, and only a value
    /// that is ALL white space is refused.
    /// </summary>
    public static TheoryData<string, string> Accepted => new()
    {
        { Balance, "" },
        { Balance, "?at=2026-09-01T00:00:00Z" },
        { List, "" },
        { List, "?FromDate=2026-09-01T00:00:00Z&ToDate=2026-09-30T00:00:00Z" },
        { List, "?AccountId={account}" },
        { Summary, "" },
        { Summary, "?FromDate=2026-09-01T00:00:00Z" },
        { Summary, "?AccountId=%20{account}%20" },
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public async Task AnAbsentOrRealValue_IsStillAccepted(string path, string query)
    {
        var (token, _, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        var response = await Client.GetAsync($"{path}{query}".Replace("{account}", accountId.ToString()));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A real date binds as the UTC instant it names, through the binder this app builds for a date
    /// in the query. The fix first wrapped the generic simple-type binder, which reads "...Z" as
    /// LOCAL time: on a UTC+2 machine every date filter moved by two hours, and the 200s above stayed
    /// green. On a machine on UTC the instant comes out right by accident and only its Kind, Local,
    /// gives it away, so the Kind is asserted too.
    /// </summary>
    [Fact]
    public async Task ARealDate_BindsAsTheUtcInstantItNames()
    {
        var metadata = Factory.Services.GetRequiredService<IModelMetadataProvider>()
            .GetMetadataForType(typeof(DateTime?));
        var bindingInfo = new BindingInfo { BindingSource = BindingSource.Query };
        var binder = Factory.Services.GetRequiredService<IModelBinderFactory>().CreateBinder(
            new ModelBinderFactoryContext { Metadata = metadata, BindingInfo = bindingInfo });
        var query = new QueryCollection(
            new Dictionary<string, StringValues> { ["at"] = "2026-09-01T00:00:00Z" });
        var context = DefaultModelBindingContext.CreateBindingContext(
            new ActionContext(
                new DefaultHttpContext { RequestServices = Factory.Services },
                new RouteData(),
                new ActionDescriptor()),
            new QueryStringValueProvider(BindingSource.Query, query, CultureInfo.InvariantCulture),
            metadata,
            bindingInfo,
            "at");

        await binder.BindModelAsync(context);

        var bound = context.Result.Model.Should().BeOfType<DateTime>().Subject;
        bound.Should().Be(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        bound.Kind.Should().Be(DateTimeKind.Utc);
    }

    /// <summary>
    /// The Step-Up-Authorization header still reads an EMPTY value as absent, as ADR-0042 decided:
    /// it binds [FromHeader] Guid? so that an empty header and a missing one both reach the service
    /// and answer 401, not 400. The rule above is for the query only. This asks the binder the app
    /// builds for that header, because an integration test could not carry the case: the in-memory
    /// server never delivered the empty header, so AnEmptyHeaderIsAnAbsentOne_NotAMalformedOne
    /// stayed green with the rule applied to headers too, while the same withdrawal sent over HTTP
    /// answered 400 {"Step-Up-Authorization":["The value '' is not valid."]} (measured 2026-09-24).
    /// </summary>
    [Fact]
    public async Task AnEmptyStepUpHeader_StillBindsAsAbsent()
    {
        var metadata = Factory.Services.GetRequiredService<IModelMetadataProvider>()
            .GetMetadataForType(typeof(Guid?));
        var bindingInfo = new BindingInfo
        {
            BindingSource = BindingSource.Header,
            BinderModelName = StepUpConstants.HeaderName,
        };
        var binder = Factory.Services.GetRequiredService<IModelBinderFactory>().CreateBinder(
            new ModelBinderFactoryContext { Metadata = metadata, BindingInfo = bindingInfo });
        var httpContext = new DefaultHttpContext { RequestServices = Factory.Services };
        httpContext.Request.Headers[StepUpConstants.HeaderName] = string.Empty;
        var context = DefaultModelBindingContext.CreateBindingContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            new CompositeValueProvider(),
            metadata,
            bindingInfo,
            StepUpConstants.HeaderName);

        await binder.BindModelAsync(context);

        context.ModelState.ErrorCount.Should().Be(0);
        context.Result.IsModelSet.Should().BeTrue();
        context.Result.Model.Should().BeNull();
    }
}
