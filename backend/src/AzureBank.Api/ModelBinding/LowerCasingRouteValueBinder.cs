using AzureBank.Shared.Validation;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace AzureBank.Api.ModelBinding;

/// <summary>
/// Binds a string route value with its capital letters lower-cased (<see cref="HandleCase"/>), so the validation attributes of the
/// parameter see the handle the store holds. It is for the payee's handle in
/// <c>GET /api/users/{azureTag}</c>: <c>Jane_02ga</c> is looked up as <c>jane_02ga</c>, and the
/// answer names the lower-case handle.
///
/// The framework's own steps stay: the raw value is recorded in the model state under the
/// parameter's name, so a refusal still reports under <c>azureTag</c>.
/// </summary>
public sealed class LowerCasingRouteValueBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var value = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        if (value == ValueProviderResult.None)
        {
            return Task.CompletedTask;
        }

        bindingContext.ModelState.SetModelValue(bindingContext.ModelName, value);
        bindingContext.Result = ModelBindingResult.Success(value.FirstValue is { } text ? HandleCase.Lower(text) : null);
        return Task.CompletedTask;
    }
}
