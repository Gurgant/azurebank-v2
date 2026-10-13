using AzureBank.Api.Attributes;
using AzureBank.Api.Transformers;
using AzureBank.Shared.Constants;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace AzureBank.Tests.Unit.Transformers;

/// <summary>
/// Two transformers of the published contract refuse, and say why, where what they complete is
/// not there. Their refusals ran in no test.
/// </summary>
/// <remarks>
/// Each of the two completes something another part of the pipeline emits: the
/// <c>ProblemDetails</c> component, and the <c>Step-Up-Authorization</c> header of an action that
/// carries the marker. If that part changes shape, a transformer that returned quietly would
/// publish a contract that omits what the API requires, and the gate that compares the document
/// to the code would compare two things wrong in the same direction. So both throw, and the
/// document is not written.
/// </remarks>
public sealed class ContractTransformerRefusalTests
{
    private static OpenApiOperationTransformerContext AnAction(params object[] metadata) =>
        new()
        {
            DocumentName = "v1",
            Description = new ApiDescription
            {
                RelativePath = "api/transactions/withdraw",
                ActionDescriptor = new ActionDescriptor { EndpointMetadata = metadata },
            },
            ApplicationServices = new ServiceCollection().BuildServiceProvider(),
        };

    [Fact]
    public async Task WithNoProblemDetailsComponent_TheDocumentIsRefused_AndTheReasonNamesIt()
    {
        var context = new OpenApiDocumentTransformerContext
        {
            DocumentName = "v1",
            DescriptionGroups = [],
            ApplicationServices = new ServiceCollection().BuildServiceProvider(),
        };

        var transform = () => new ProblemDetailsExtensionsTransformer()
            .TransformAsync(new OpenApiDocument(), context, CancellationToken.None);

        (await transform.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*'ProblemDetails'*");
    }

    [Fact]
    public async Task AMarkedActionWithNoHeaderParameter_IsRefused_AndTheReasonNamesTheRouteAndTheHeader()
    {
        var operation = new OpenApiOperation
        {
            Parameters = [new OpenApiParameter { Name = "Idempotency-Key", In = ParameterLocation.Header }],
        };

        var transform = () => new StepUpAuthorizationOperationTransformer()
            .TransformAsync(operation, AnAction(new RequireStepUpAuthorizationAttribute()), CancellationToken.None);

        (await transform.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"*api/transactions/withdraw*'{StepUpConstants.HeaderName}'*");
    }

    [Fact]
    public async Task AMarkedActionWithItsHeaderParameter_PublishesItAsRequired()
    {
        // CONTROL: the refusal above is for the missing parameter, not for every marked action.
        var header = new OpenApiParameter { Name = StepUpConstants.HeaderName, In = ParameterLocation.Header };
        var operation = new OpenApiOperation { Parameters = [header] };

        await new StepUpAuthorizationOperationTransformer()
            .TransformAsync(operation, AnAction(new RequireStepUpAuthorizationAttribute()), CancellationToken.None);

        header.Required.Should().BeTrue();
    }

    [Fact]
    public async Task AnActionWithoutTheMarker_IsLeftAlone_EvenWithNoHeaderParameter()
    {
        var operation = new OpenApiOperation();

        var transform = () => new StepUpAuthorizationOperationTransformer()
            .TransformAsync(operation, AnAction(), CancellationToken.None);

        await transform.Should().NotThrowAsync();
    }
}
