import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { beforeAll, describe, expect, it } from 'vitest';
import { asProblem, call, login } from './client';

/**
 * The published DOCUMENT and the answering STACK, checked against each other.
 *
 * Every other file here compares the mock to the real backend. This one adds the third party that
 * was never in the room: `docs/api/openapiv1.json`, from which the frontend's types and its runtime
 * Zod validators are generated. Nothing gated it against reality — the drift job regenerates those
 * artefacts FROM the document and compares them, which proves the generated code matches the
 * document and never that the document matches the server. Schemathesis could have, and its step
 * ends with `|| true  # report, don't gate`.
 *
 * What that gap allowed, measured on 2026-08-18 before this existed: 58 declared responses said the
 * body was empty, while all 53 that could actually occur answered `application/json` with seven
 * keys, and the other 5 described responses the code cannot produce at all.
 *
 * So each row below asserts the same fact twice — the stack sends it, and the document says it will.
 * Running against both targets means a mock that stops sending `errorCode` fails too.
 */

const SPEC_URL = new URL('../../../docs/api/openapiv1.json', import.meta.url);

interface OpenApiResponse {
  content?: Record<string, { schema?: { $ref?: string } }>;
}

interface OpenApiDocument {
  paths: Record<string, Record<string, { responses?: Record<string, OpenApiResponse> }>>;
  components: { schemas: Record<string, { properties?: Record<string, unknown> }> };
}

const spec = JSON.parse(readFileSync(fileURLToPath(SPEC_URL), 'utf8')) as OpenApiDocument;

/**
 * The declared body for one operation and status, resolved through `$ref` to its properties.
 *
 * Returns null when the response is declared with NO body — which is the exact state this file
 * exists to keep from coming back, so the assertions treat null as a failure rather than skipping.
 */
function declaredProperties(path: string, method: string, status: string): string[] | null {
  const response = spec.paths[path]?.[method]?.responses?.[status];
  const media = response?.content?.['application/json'];
  if (!media) return null;

  const ref = media.schema?.$ref;
  if (!ref) return null;

  const component = spec.components.schemas[ref.replace('#/components/schemas/', '')];
  return Object.keys(component?.properties ?? {});
}

beforeAll(async () => {
  expect((await login()).status).toBe(200);
});

describe('contract: the document describes the errors the stack sends', () => {
  it('answers an unknown account with a ProblemDetails the document declares', async () => {
    // Observed on the real API, GET /api/accounts/{unknown-guid}:
    // {"type":"https://httpstatuses.com/404","title":"Not Found","status":404,
    //  "detail":"Account with identifier '…' was not found.","instance":"/api/accounts/…",
    //  "errorCode":"ACCOUNT_NOT_FOUND","traceId":"d23fb0a6631cbb55fc84a32313f15191"}
    const unknown = '6fe6510d-0000-4000-8000-000000000000';
    const { status, body } = await call(`/api/accounts/${unknown}`);
    const problem = asProblem(body);

    expect(status).toBe(404);
    expect(problem.errorCode).toBe('ACCOUNT_NOT_FOUND');
    expect(problem.status).toBe(404);

    const declared = declaredProperties('/api/accounts/{id}', 'get', '404');
    expect(declared).not.toBeNull();
    expect(declared).toContain('errorCode');
    expect(declared).toContain('traceId');
  });

  it('declares a body on every refusal the shared transformers fill in', () => {
    /*
      The whole class, not one sample. A row-by-row HTTP probe of all 53 would need a second account
      and a stolen token; what a client is actually harmed by is the DECLARATION being empty, and
      that is checkable here in full. The backend's PublishedErrorContractTests asserts the same
      property from the other side, and this one keeps it inside the suite the frontend runs.
    */
    const empties: string[] = [];
    let scanned = 0;

    for (const [path, operations] of Object.entries(spec.paths)) {
      for (const [method, operation] of Object.entries(operations)) {
        for (const [status, response] of Object.entries(operation.responses ?? {})) {
          scanned += 1;
          if (!['401', '403', '404'].includes(status)) continue;
          if (!Object.keys(response.content ?? {}).length) {
            empties.push(`${method.toUpperCase()} ${path} ${status}`);
          }
        }
      }
    }

    // A scan that reads nothing passes every assertion under it; 132 responses on the day written.
    expect(scanned).toBeGreaterThan(100);
    expect(empties).toEqual([]);
  });

  it('does not promise a 404 from the handle lookup, which answers 200 with exists:false', async () => {
    /*
      ADR-0014's enumeration-neutral oracle. The document used to declare a 404 here purely because
      the route template contains a brace — a response the endpoint cannot produce, and one a client
      would read as "no such user" while the real answer arrives as 200.
    */
    const { status, body } = await call('/api/users/nobody_here_at_all');
    const envelope = body as { data?: { exists?: boolean } };

    expect(status).toBe(200);
    expect(envelope.data?.exists).toBe(false);
    expect(spec.paths['/api/users/{azureTag}']?.get?.responses?.['404']).toBeUndefined();
  });
});
