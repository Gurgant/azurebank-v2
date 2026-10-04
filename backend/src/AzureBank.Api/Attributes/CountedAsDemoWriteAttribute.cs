namespace AzureBank.Api.Attributes;

/// <summary>
/// Marks an endpoint whose method is a safe one and which writes all the same. On the public demo
/// <c>DemoWriteBudgetMiddleware</c> reads it from the endpoint metadata and counts the request
/// against its copy's budget of changes (<c>Demo:Copy:MaxWrites</c>), as it counts every request
/// whose method is not a safe one.
/// </summary>
/// <remarks>
/// <para>
/// One endpoint carries it: the reveal of a full account number, a GET that writes a row of the
/// audit trail on every call (<c>AccountService.GetFullAccountNumberAsync</c>). The budget bounds
/// what one copy can leave behind, and a copy's audit rows are what stays when the copy is deleted
/// (ADR-0062, decision 11). A budget that counted by method alone would not bound them.
/// </para>
/// <para>
/// A marker rather than a check inside the action, for the reason <see cref="DemoOnlyAttribute"/>
/// is one: the budget is spent in middleware, before model binding and before the action, so a
/// request past the limit is refused before anything is written for it.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CountedAsDemoWriteAttribute : Attribute
{
}
