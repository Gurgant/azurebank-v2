import { modelStateProblem } from './problem';

/**
 * The DataAnnotations gate — the 400 that fires BEFORE a handler's body ever runs.
 *
 * `/bff/auth/login` and `/bff/auth/register` bind `[FromBody] LoginRequest` / `RegisterRequest`,
 * the SAME shared DTOs the API binds (`BffAuthController.cs:71,138`). So `[ApiController]`'s
 * automatic model-state short-circuit runs inside the BFF and the request never reaches the proxy
 * call at all. That matters for two reasons the mock has to respect:
 *
 *   - the envelope is the FRAMEWORK one — rfc9110 type, no `instance`, no `errorCode`, a W3C
 *     traceparent — and not the API's handler-written shape, even though the DTO is the API's;
 *   - the gate runs before the credential check, so a malformed password can never touch the
 *     login failure counter.
 *
 * Measured on the BFF (:5000), 2026-08-07 — the surface the mock intercepts, which is the whole
 * point of measuring here rather than on :7215:
 *
 *   {"email":"nobody@azurebank.dev","password":"ValidPass1!"}  -> 401 INVALID_CREDENTIALS
 *   {"email":"a@b.dev","password":"Ab1!"}                      -> 400, Password format only
 *
 * The mock used to answer 401 for the second one, because it compared credentials and never
 * modelled the gate.
 *
 * ─── The three semantics that are easy to get wrong, all measured ───────────────────────────
 *
 * 1. ABSENT is not the same as NULL. A missing member fails System.Text.Json deserialisation
 *    (the DTO members are C# `required`), so it never reaches model validation and answers with
 *    the `$` / `request` pair naming the CLR type — the same envelope shape `unreadableBodyProblem`
 *    produces for unparseable JSON, with a different sentence:
 *
 *      {} -> {"$":["JSON deserialization for type 'AzureBank.Shared.DTOs.Auth.LoginRequest' was
 *                   missing required properties including: 'email', 'password'."],
 *             "request":["The request field is required."]}
 *
 * 2. NULL fires `[Required]` ALONE; empty or whitespace fires Required AND every format rule.
 *    That is DataAnnotations' rule — validators other than Required skip null but do run on "" —
 *    and `RequiredAttribute` counts whitespace as missing:
 *
 *      {"email":null,"password":null}  -> Email/Password: ["The … field is required."]
 *      {"email":"   ","password":"   "} -> both, PLUS both format messages
 *
 * 3. The two DTOs word the same length rule DIFFERENTLY, because login uses `[MaxLength]` and
 *    register uses `[StringLength]`. Note the quotes around the number on one and not the other:
 *
 *      login    -> "The field Email must be a string or array type with a maximum length of '255'."
 *      register -> "The field Email must be a string with a maximum length of 255."
 *
 * Key casing is PascalCase throughout because the values were bound to DTO PROPERTIES — the rule
 * established in PR #75, where casing follows how a value was BOUND rather than who produced it.
 */

/** A rule other than `[Required]`. Runs on "" and on whitespace, never on null. */
type FormatRule = {
  fails: (value: string) => boolean;
  message: string;
};

type FieldSpec = {
  /** The ModelState key, PascalCase — the DTO property name. */
  key: string;
  /** The JSON member the client sends, camelCase. */
  json: string;
  /** `[Required]`'s message for this field. */
  required: string;
  /**
   * Declaration order matters: when two rules fail, both messages appear in the order the
   * attributes appear on the property. Measured on a 300-character non-email, which trips
   * `[EmailAddress]` and `[MaxLength]` together and reports them in that order.
   */
  format: FormatRule[];
  /**
   * `FirstName`/`LastName` trim in their SETTERS, so validation sees the normalised value —
   * `"  a  "` is a 1-character name, not a 5-character one, and is rejected as such.
   */
  trims?: true;
};

type RequestSpec = {
  /** Spelled exactly as the deserialiser reports it in the `$` message. */
  clrType: string;
  fields: FieldSpec[];
};

/**
 * `EmailAddressAttribute` is far more permissive than its name suggests: it wants exactly one `@`
 * with something on each side, and nothing else. Modelling it as a strict RFC address would reject
 * inputs the backend accepts, which is the wrong direction for a mock to be wrong in.
 */
const looksLikeEmail = (value: string) => {
  // CR and LF are refused before the `@` is even located — a reviewer raised it and the wire
  // agreed. Measured 2026-08-07: {"email":"a@\nb.dev"} -> 400 "The Email field is not a valid
  // e-mail address.", where the rule below on its own would have called it well-formed and let it
  // through to the credential check.
  if (value.includes('\r') || value.includes('\n')) return false;
  const at = value.indexOf('@');
  return at > 0 && at === value.lastIndexOf('@') && at < value.length - 1;
};

/** `ValidationRules.PasswordPattern` verbatim — the length bound lives inside the pattern. */
const PASSWORD_PATTERN = /^(?=.*[a-z])(?=.*[A-Z])(?=.*[0-9])(?=.*[^a-zA-Z0-9])[\x20-\x7E]{8,128}$/;

/** `ValidationRules.NamePattern` verbatim, accented letters included. */
const NAME_PATTERN = /^[a-zA-ZÀ-ÖØ-öø-ÿ\s'-]{2,50}$/;

/** `ValidationRules.AzureTagPattern` verbatim. */
const AZURE_TAG_PATTERN = /^[a-z][a-z0-9_]{2,19}$/;

/**
 * One string, not two. The `[Password]` attribute concatenates its length sentence and
 * `ValidationRules.PasswordPatternMessage` with " and ", so a reader looking for two array entries
 * will not find them.
 */
const PASSWORD_MESSAGE =
  'Password must be 8-128 characters and Password must contain at least one uppercase, ' +
  'one lowercase, one digit, and one special character.';

const passwordField = (): FieldSpec => ({
  key: 'Password',
  json: 'password',
  required: 'The Password field is required.',
  format: [{ fails: (v) => !PASSWORD_PATTERN.test(v), message: PASSWORD_MESSAGE }],
});

const nameField = (which: 'First' | 'Last'): FieldSpec => ({
  key: `${which}Name`,
  json: `${which.toLowerCase()}Name`,
  required: `The ${which}Name field is required.`,
  format: [
    {
      fails: (v) => v.length < 2 || v.length > 50,
      message: `${which} name must be between 2 and 50 characters.`,
    },
    {
      fails: (v) => !NAME_PATTERN.test(v),
      message: 'Name can only contain letters, spaces, hyphens, and apostrophes.',
    },
  ],
  trims: true,
});

/**
 * `LoginRequest` — and note it validates the password's COMPLEXITY on a path that merely VERIFIES
 * one. `BffReauthenticateRequest` documents at length why it declines to do the same ("a wrong
 * guess that fails complexity would answer 400 where every other wrong guess answers 401 — a free
 * oracle", and a password set under an older policy would be refused outright). The mock mirrors
 * what login actually does rather than what that docblock argues it should; the divergence between
 * the two endpoints is the backend's to resolve, not the mock's to paper over.
 */
export const LOGIN_REQUEST: RequestSpec = {
  clrType: 'AzureBank.Shared.DTOs.Auth.LoginRequest',
  fields: [
    {
      key: 'Email',
      json: 'email',
      required: 'The Email field is required.',
      format: [
        {
          fails: (v) => !looksLikeEmail(v),
          message: 'The Email field is not a valid e-mail address.',
        },
        {
          fails: (v) => v.length > 255,
          message: "The field Email must be a string or array type with a maximum length of '255'.",
        },
      ],
    },
    passwordField(),
  ],
};

/** `RegisterRequest` — five fields, and the email length message is worded unlike login's. */
export const REGISTER_REQUEST: RequestSpec = {
  clrType: 'AzureBank.Shared.DTOs.Auth.RegisterRequest',
  fields: [
    {
      key: 'AzureTag',
      json: 'azureTag',
      required: 'The AzureTag field is required.',
      format: [
        {
          fails: (v) => !AZURE_TAG_PATTERN.test(v),
          message:
            'AzureTag must start with a letter and contain only lowercase letters, numbers, and underscores.',
        },
      ],
    },
    {
      key: 'Email',
      json: 'email',
      required: 'The Email field is required.',
      format: [
        {
          fails: (v) => !looksLikeEmail(v),
          message: 'The Email field is not a valid e-mail address.',
        },
        {
          fails: (v) => v.length > 255,
          message: 'The field Email must be a string with a maximum length of 255.',
        },
      ],
    },
    passwordField(),
    nameField('First'),
    nameField('Last'),
  ],
};

/**
 * Returns the 400 the framework would have written, or `null` to let the handler proceed.
 *
 * Deliberately takes the ALREADY-PARSED body: unparseable JSON is a different failure with a
 * different sentence, and `unreadableBodyProblem` owns it. Call this immediately after that check
 * and before anything that touches state — on the real thing nothing downstream has run yet.
 */
export function modelStateFor(body: Record<string, unknown>, spec: RequestSpec): Response | null {
  /*
    A member of the WRONG JSON TYPE never reaches validation either — System.Text.Json cannot put a
    number into a `string`, so it fails at the same stage as an absent member but keys the error by
    the PATH to the member rather than by the document root. Measured 2026-08-07, one probe per
    JSON type, all four identical apart from the offset:

      {"email":123,…} / true / {} / []
      -> 400 {"request":["The request field is required."],
              "$.email":["The JSON value could not be converted to System.String.
                          Path: $.email | LineNumber: 0 | BytePositionInLine: 13."]}

    Deserialisation stops at the first bad member IN DOCUMENT ORDER, so at most one `$.x` key ever
    appears — `{"email":123,"password":456}` reports `$.email` alone.

    AND THIS CHECK COMES FIRST, ahead of the missing-member one below, which is the opposite of
    what I wrote at first. The reader meets a bad value while scanning; it only discovers what is
    MISSING once it reaches the closing brace. So a body that is both mistyped and incomplete
    reports the conversion, never the omission:

      {"password":456}  (email absent, password mistyped)
      -> "$.password": ["The JSON value could not be converted…"]   and no `$` key at all

    The trailing ` | LineNumber: n | BytePositionInLine: n.` is deliberately NOT reproduced. Those
    are offsets into the raw bytes, and the rule differs by JSON type — the four probes above
    reported 13, 14, 11 and 11 for bodies of the same shape — so matching them would mean emulating
    the reader's cursor to produce a number no consumer reads. The sentence stops at the path, and
    the tests assert that prefix on BOTH targets rather than pinning an invented offset.
  */
  // Document order, not spec order — `Object.keys` preserves what the client actually sent.
  const mistyped = Object.keys(body).find(
    (member) =>
      spec.fields.some((field) => field.json === member) &&
      body[member] !== null &&
      typeof body[member] !== 'string',
  );
  if (mistyped) {
    return modelStateProblem({
      request: ['The request field is required.'],
      [`$.${mistyped}`]: [
        `The JSON value could not be converted to System.String. Path: $.${mistyped}`,
      ],
    });
  }

  const absent = spec.fields.filter((field) => !(field.json in body));
  if (absent.length > 0) {
    return modelStateProblem({
      $: [
        `JSON deserialization for type '${spec.clrType}' was missing required properties ` +
          `including: ${absent.map((field) => `'${field.json}'`).join(', ')}.`,
      ],
      request: ['The request field is required.'],
    });
  }

  const errors: Record<string, string[]> = {};
  for (const field of spec.fields) {
    const raw = body[field.json];
    const messages: string[] = [];

    // Whitespace counts as missing — `RequiredAttribute` trims before testing unless
    // AllowEmptyStrings is set, and nothing here sets it.
    if (raw === null || (typeof raw === 'string' && raw.trim().length === 0)) {
      messages.push(field.required);
    }
    if (typeof raw === 'string') {
      const value = field.trims ? raw.trim() : raw;
      messages.push(
        ...field.format.filter((rule) => rule.fails(value)).map((rule) => rule.message),
      );
    }

    if (messages.length > 0) errors[field.key] = messages;
  }

  return Object.keys(errors).length > 0 ? modelStateProblem(errors) : null;
}
