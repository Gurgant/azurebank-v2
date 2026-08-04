import { describe, expect, it } from 'vitest';
import { toFieldName } from './validationErrors';

/**
 * Each case is a key the API was OBSERVED emitting on the running stack (2026-08-04), not a key
 * invented to make a transform look reasonable. The envelope each one comes from is named, because
 * the reason this function exists at all is that there is more than one envelope.
 */
describe('toFieldName', () => {
  it('camel-cases a model-state key, which the API sends PascalCase', () => {
    // POST /api/accounts {"name":"x","type":"Checking"} -> 400
    // {"title":"One or more validation errors occurred.","errors":{"Name":[...]}}
    expect(toFieldName('Name')).toBe('name');
    // POST /bff/auth/register (invalid) -> 400, keys Email / AzureTag / FirstName / LastName.
    expect(toFieldName('Email')).toBe('email');
    expect(toFieldName('AzureTag')).toBe('azureTag');
    expect(toFieldName('FirstName')).toBe('firstName');
  });

  it('leaves a FluentValidation key alone, which the API already sends camelCase', () => {
    // POST /api/transactions/deposit {"amount":10.123} -> 400
    // {"title":"Validation Failed","errors":{"amount":["Amount cannot have more than 2 decimal places."]}}
    expect(toFieldName('amount')).toBe('amount');
    // POST /api/accounts {"name":"Valid Name","type":"99"} -> 400
    // {"title":"Validation Failed","errors":{"type":["Invalid account type. Must be Checking, …"]}}
    expect(toFieldName('type')).toBe('type');
  });

  it('leaves a parameter-bound model-state key alone — that envelope is NOT always PascalCase', () => {
    /*
      The trap this guards. Model state keys by the BINDING NAME, not by a fixed casing, so the one
      framework envelope emits both:

        GET /api/transactions/summary?fromDate=garbage
          -> {"errors":{"FromDate":["The value 'garbage' is not valid for FromDate."]}}   (property)
        GET /api/accounts/{id}/balance?at=garbage
          -> {"errors":{"at":["The value 'garbage' is not valid."]}}                      (parameter)

      Both measured. A transform that "restored" PascalCase, or that keyed off the envelope's title
      to decide the casing, would corrupt the second one.
    */
    expect(toFieldName('at')).toBe('at');
    expect(toFieldName('FromDate')).toBe('fromDate');
  });

  it('does not invent a field name for a model-binding key', () => {
    /*
      POST /api/accounts {"name":"Valid Name","type":12345} -> 400
      {"errors":{"request":["The request field is required."],"$.type":["Integer values are not …"]}}

      `$` has no lower case, so the key comes back unchanged and matches no form field — which is
      the CORRECT outcome. Both consumers treat an unmatched key as a form-level error, so the
      message still reaches the user instead of being silently dropped. Asserted so that a future
      "improvement" that strips `$.` cannot quietly start aiming these at a field.
    */
    expect(toFieldName('$.type')).toBe('$.type');
    expect(toFieldName('request')).toBe('request');
  });

  it('is total — an empty key does not throw', () => {
    // Not observed from the API; asserted because `charAt(0)` on '' is '' and a crash inside a
    // catch block would replace a validation message with a blank screen.
    expect(toFieldName('')).toBe('');
  });

  it('changes only the first character, never the rest of the key', () => {
    // Guards the "deliberately not a general camelCase converter" decision in the docblock: a
    // separator-aware rewrite would break these, and no server envelope produces such keys.
    expect(toFieldName('AzureTag')).toBe('azureTag');
    expect(toFieldName('ABC')).toBe('aBC');
  });
});
