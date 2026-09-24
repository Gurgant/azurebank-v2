using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace AzureBank.Api.ModelBinding;

/// <summary>
/// Refuses a query value that is PRESENT but empty, <c>?ToDate=</c> or <c>?AccountId=</c>, instead
/// of reading it as absent. The framework binds an empty or whitespace-only string to a nullable
/// value type as null, so the API answered 200 to values the contract's formats call invalid
/// (<c>date-time</c>, <c>uuid</c>), while <c>=garbage</c> answered 400. Measured 2026-09-24 on all
/// seven nullable query parameters: the balance's <c>at</c>, and <c>FromDate</c>, <c>ToDate</c> and
/// <c>AccountId</c> on the transaction list and on its summary. Empty and whitespace-only both
/// answered 200. Now they answer 400 the way garbage does: same error key, same wording.
/// </summary>
public sealed class EmptyQueryValueRejectingBinder(IModelBinder inner) : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var value = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        if (value == ValueProviderResult.None || !string.IsNullOrWhiteSpace(value.FirstValue))
        {
            return inner.BindModelAsync(bindingContext);
        }

        // The route the framework's own binders take for "garbage": record the attempted value,
        // then a FormatException, which the model state turns into the standard "is not valid"
        // message, worded for a parameter or for a property exactly as the framework words it.
        bindingContext.ModelState.SetModelValue(bindingContext.ModelName, value);
        bindingContext.ModelState.TryAddModelError(
            bindingContext.ModelName, new FormatException(), bindingContext.ModelMetadata);
        bindingContext.Result = ModelBindingResult.Failed();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Puts <see cref="EmptyQueryValueRejectingBinder"/> in front of the binder the framework would
/// pick for a nullable value type bound from the query.
/// </summary>
public sealed class EmptyQueryValueRejectingBinderProvider(IList<IModelBinderProvider> providers)
    : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        if (Nullable.GetUnderlyingType(context.Metadata.ModelType) is null)
        {
            return null;
        }

        // The query only. A [FromQuery] parameter says Query; a property of a [FromQuery] filter
        // object says nothing and binds from its parent's source. Anything else keeps the
        // framework's reading of an empty value, above all the Step-Up-Authorization header: it
        // binds [FromHeader] Guid? so that an empty header is an absent one and answers 401, not
        // 400 (ADR-0042; EmptyQueryValueTests.AnEmptyStepUpHeader_StillBindsAsAbsent pins it, and
        // the two ABlankHeaderIsAnAbsentOne_NotAMalformedOne tests end to end). The header binder
        // builds its inner binder under BindingSource.ModelBinding, so that is left alone too.
        var source = context.BindingInfo.BindingSource;
        if (source is not null && source != BindingSource.Query)
        {
            return null;
        }

        // Wraps the binder the framework itself would pick, found by asking the providers after
        // this one. For DateTime? that is the date-time binder, which reads a value as UTC
        // (DateTimeStyles.AdjustToUniversal); the generic simple-type binder reads "...Z" as LOCAL
        // time, and the filters compare it with UTC timestamps. Measured 2026-09-24 with that one
        // wrapped instead, on a UTC+2 machine: ?FromDate an hour before a deposit left the deposit
        // out, and ?ToDate an hour before it kept it in.
        foreach (var provider in providers)
        {
            if (!ReferenceEquals(provider, this) && provider.GetBinder(context) is { } inner)
            {
                return new EmptyQueryValueRejectingBinder(inner);
            }
        }

        return null;
    }
}
