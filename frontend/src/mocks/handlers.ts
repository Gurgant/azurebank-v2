import { http, HttpResponse } from 'msw';
import { createOpenApiHttp } from 'openapi-msw';
import type { paths } from '../api/schema';
import { problem } from './problem';
import { mockState } from './state';

/**
 * Contract-faithful MSW handlers. Success bodies go through openapi-msw's TYPED response
 * helper — they must compile against the generated schema.d.ts, which is exactly the
 * enforcement this scaffolding exists for. Error bodies use the shared problem() builder.
 * Edge-case semantics are derived from the BACKEND SOURCE, not intuition:
 *  - idempotency fingerprints the RAW body bytes (IdempotencyMiddleware hashes bytes);
 *  - replay returns the stored bytes verbatim + `Idempotency-Replayed: true`;
 *  - the step-up 403 body is AuthLevelMiddleware's bare shape, NOT ProblemDetails;
 *  - a wrong PIN is HTTP 200 with verified:false (BffAuthController), not a 4xx.
 */
const api = createOpenApiHttp<paths>({ baseUrl: '*' });

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const NIL_UUID = '00000000-0000-0000-0000-000000000000';

/** FNV-1a over the raw body string — a cheap stand-in for the backend's byte HMAC. */
function fingerprint(raw: string): string {
  let hash = 0x811c9dc5;
  for (let i = 0; i < raw.length; i++) {
    hash ^= raw.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193);
  }
  return (hash >>> 0).toString(16);
}

/** POST /api/transactions/deposit — the stateful idempotency protocol (ADR-0009). */
const deposit = api.post('/api/transactions/deposit', async ({ request, response }) => {
  const key = request.headers.get('Idempotency-Key');
  if (!key) {
    return response.untyped(
      problem({
        status: 400,
        errorCode: 'IDEMPOTENCY_KEY_MISSING',
        detail: 'The Idempotency-Key header is required.',
      }),
    );
  }
  if (!UUID_RE.test(key) || key === NIL_UUID) {
    return response.untyped(
      problem({
        status: 400,
        errorCode: 'IDEMPOTENCY_KEY_INVALID',
        detail: 'The Idempotency-Key header must be a single non-empty UUID.',
      }),
    );
  }

  const raw = await request.clone().text();
  const fp = fingerprint(raw);
  const stored = mockState.idempotency.get(`deposit|${key}`);
  if (stored) {
    if (stored.bodyFingerprint !== fp) {
      return response.untyped(
        problem({
          status: 422,
          errorCode: 'IDEMPOTENCY_KEY_REUSE',
          detail: 'This idempotency key was already used with a different payload.',
        }),
      );
    }
    // Replay: the STORED bytes, verbatim, plus the replay marker header.
    return response.untyped(
      new HttpResponse(stored.body, {
        status: stored.status,
        headers: { 'Content-Type': 'application/json', 'Idempotency-Replayed': 'true' },
      }),
    );
  }

  const body = JSON.parse(raw) as { accountId?: string; amount?: number };
  const amount = body.amount ?? 0;
  const payload = {
    data: {
      transaction: {
        id: '019f7b3f-0000-7000-8000-000000000001',
        transactionNumber: 'TXN-20260720-000001',
        type: 'Deposit' as const,
        amount,
        balanceAfter: 1000 + amount,
        description: null,
        recipientAzureTag: null,
        senderAzureTag: null,
        status: 'Completed' as const,
        createdAt: '2026-07-20T12:00:00.0000000Z',
      },
      newBalance: 1000 + amount,
    },
    message: 'Deposit completed successfully.',
  };
  const text = JSON.stringify(payload);
  mockState.idempotency.set(`deposit|${key}`, { bodyFingerprint: fp, status: 201, body: text });

  // Round-trip through the TYPED helper so the success shape is compile-checked against
  // schema.d.ts (the stored string above replays byte-identically on retries).
  return response(201).json(payload);
});

/** POST /api/transfers — level-2 step-up gate (AuthLevelMiddleware semantics). */
const transfer = api.post('/api/transfers', async ({ request, response }) => {
  if (mockState.authLevel < 2) {
    // The 403 body is AuthLevelMiddleware's BARE shape — deliberately not ProblemDetails.
    return response.untyped(
      HttpResponse.json(
        {
          type: 'STEP_UP_REQUIRED',
          title: 'PIN Verification Required',
          detail: 'This operation requires PIN verification',
          requiredLevel: 2,
          currentLevel: mockState.authLevel,
          status: 403,
        },
        {
          status: 403,
          headers: {
            'X-Auth-Level-Required': '2',
            'X-Auth-Level-Current': String(mockState.authLevel),
          },
        },
      ),
    );
  }

  const body = (await request.clone().json()) as { amount?: number; recipientAzureTag?: string };
  return response(201).json({
    data: {
      transactionNumber: 'TXN-20260720-000002',
      amount: body.amount ?? 0,
      newBalance: 1000 - (body.amount ?? 0),
      recipientAzureTag: body.recipientAzureTag ?? 'someone',
      recipientName: 'John D.',
      processedAt: '2026-07-20T12:00:00.0000000Z',
    },
    message: 'Transfer completed successfully.',
  });
});

/**
 * POST /bff/auth/verify-pin — BFF endpoint (outside the API spec, so plain msw).
 * Source semantics: correct PIN elevates the session to level 2 for 5 minutes; a WRONG
 * PIN is HTTP 200 with verified:false, never a 4xx (PIN lockout is a separate 429).
 */
const MOCK_PIN = '123456';
const verifyPin = http.post('*/bff/auth/verify-pin', async ({ request }) => {
  const { pin } = (await request.json()) as { pin?: string };
  if (pin === MOCK_PIN) {
    mockState.authLevel = 2;
    return HttpResponse.json({
      data: { verified: true, authLevel: 2, pinExpiresAt: '2026-07-20T12:05:00.0000000Z' },
      message: 'PIN verified.',
    });
  }
  return HttpResponse.json({
    data: { verified: false, authLevel: mockState.authLevel, pinExpiresAt: null },
    message: 'PIN verification failed.',
  });
});

export const handlers = [deposit, transfer, verifyPin];
