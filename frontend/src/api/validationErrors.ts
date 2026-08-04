/**
 * Normalising a server validation-error KEY into a form field name.
 *
 * A 400 from this API can carry FOUR different key shapes, from TWO different producers, and the
 * casing is not a property of the producer — it is a property of HOW THE VALUE WAS BOUND. Getting
 * that wrong decides whether a server rule lands on the field the user must fix or disappears into
 * a generic bar, so every shape below is quoted from a response observed on the running stack
 * (2026-08-04). None of it is inferred from the C#.
 *
 * ---------------------------------------------------------------------------------------------
 * PRODUCER 1 — the framework. `[ApiController]` model state / model binding.
 *   title "One or more validation errors occurred." · rfc9110 `type` · NO `detail`, NO `instance`
 *
 *   It keys by the BINDING NAME, which is why one producer emits three different shapes:
 *
 *   (a) a DTO property -> PascalCase, the CLR name.
 *       POST /api/accounts {"name":"x","type":"Checking"}
 *       -> {"errors":{"Name":["Account name must be between 2 and 100 characters."]}}
 *       GET /api/transactions/summary?fromDate=garbage
 *       -> {"errors":{"FromDate":["The value 'garbage' is not valid for FromDate."]}}
 *
 *   (b) an action PARAMETER -> camelCase, the C# parameter name. Same envelope, other casing.
 *       GET /api/accounts/{id}/balance?at=garbage
 *       -> {"errors":{"at":["The value 'garbage' is not valid."]}}
 *       (Note the message loses the " for <Name>." suffix that the property form carries.)
 *
 *   (c) JSON deserialisation failed -> a JSON path, plus the body parameter's own name.
 *       POST /api/accounts {"name":"Valid Name","type":12345}
 *       -> {"errors":{"request":["The request field is required."],
 *                     "$.type":["Integer values are not allowed for enum 'AccountType'. …"]}}
 *
 * PRODUCER 2 — FluentValidation, via the API's `ValidationExceptionHandler`.
 *   title "Validation Failed" · `detail` · `instance` · httpstatuses `type` · ALWAYS camelCase
 *   (the handler runs `ToCamelCase(PropertyName)`, so the binding name never shows through).
 *
 *       POST /api/accounts {"name":"Valid Name","type":"99"}
 *       -> {"title":"Validation Failed","instance":"/api/accounts",
 *           "errors":{"type":["Invalid account type. Must be Checking, Savings, or Investment."]}}
 *
 *   Easy to believe dead, and it is not. `[ApiController]` validates model state BEFORE the action
 *   body, so a DataAnnotation failure short-circuits and the controller's `ValidateAndThrowAsync`
 *   never runs — and most validators here merely restate their annotations. It is reachable only
 *   where a validator is STRICTER than every annotation on that property. Three such gaps exist:
 *   `CreateAccountRequest.Type` has no annotation at all (the response above: `"99"` is a JSON
 *   string, so the strict enum converter hands it to `Enum.TryParse`, which accepts the numeric
 *   form and binds an undefined member for `IsInEnum()` to reject); `[MoneyRange]` checks
 *   magnitude but not scale, so `10.123` reaches `.ValidMoneyScale()`; and "transfer to the same
 *   account" is cross-field, which DataAnnotations on these DTOs cannot express.
 * ---------------------------------------------------------------------------------------------
 *
 * So lower-casing the first character is not tidiness: shape (a) needs it, and it must not damage
 * the shapes that are already camelCase. It is deliberately the WEAKEST transform that does that.
 *
 * - (a) `'Name'` -> `'name'`, `'FromDate'` -> `'fromDate'` — the whole point.
 * - (b) and producer 2 are already camelCase and pass through untouched.
 * - (c) `'$.type'` -> `'$.type'` (`$` has no lower case), so it matches no field. That is CORRECT:
 *   every consumer treats an unmatched key as a form-level error, so the message still reaches the
 *   user instead of being aimed at a field that does not exist.
 *
 * Deliberately NOT a general camelCase converter — it does not touch `_`, `-` or `.` separators
 * and does not split words, because no shape above produces those. Handling cases the server
 * cannot emit would be untested code pretending to be a contract.
 *
 * HONEST SCOPE. The app's own forms currently block every payload that reaches producer 2:
 * `moneySchemas` clamps amounts to two decimals and refuses a same-account transfer, and the
 * account-type control is a three-option select. Producer 2 was reached here with curl, not by the
 * SPA. Producer 1 is the one the app actually meets, whenever a client rule and a server rule
 * drift. So this function earns its place as insurance against a form that later drops one of
 * those client-side guards — not because camelCase keys arrive every day.
 *
 * There is also a fourth 400 with NO `errors` member at all (`InvalidRequestMiddleware`, for a
 * malformed HTTP request). `problemBaseQuery` gives it `errorCode: 'HTTP_400'`, never
 * `'VALIDATION_ERROR'`, so it never reaches this function — callers branch it to the error bar.
 */
export const toFieldName = (key: string): string => key.charAt(0).toLowerCase() + key.slice(1);
