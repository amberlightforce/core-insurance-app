import { expect, type APIRequestContext } from '@playwright/test';
import { call, eventually, signIn, type Json } from './api.js';

// Slice 3 helpers (SL3-E2E-HARNESS): the time-shifted environment and complete-set journal waits.

/** Advances the shiftable dev clock (POST /api/plt/v1/dev/clock/advance, Platform.Admin, forward-only; D-SL3-12). */
export async function advanceClock(request: APIRequestContext, advance: { days?: number; hours?: number }): Promise<Json> {
  const admin = await signIn(request, 'admin');
  const result = await call(request, admin, 'POST', '/api/plt/v1/dev/clock/advance', { days: advance.days ?? 0, hours: advance.hours ?? 0 });
  expect(result.status, `advance the dev clock: ${result.text}`).toBe(200);
  return result.body;
}

/** Current offset and now of the shiftable dev clock. */
export async function readClock(request: APIRequestContext): Promise<Json> {
  const admin = await signIn(request, 'admin');
  const result = await call(request, admin, 'GET', '/api/plt/v1/dev/clock');
  expect(result.status, `read the dev clock: ${result.text}`).toBe(200);
  return result.body;
}

/**
 * Waits until the journals of a policy are exactly the expected set, each entry as "sourceModule:sourceEventType" (the
 * complete set, never the first match; PITFALLS 29). Returns the journals.
 */
export async function waitForJournals(request: APIRequestContext, token: string, policyNumber: string, expectedEntryTypes: string[]): Promise<Json[]> {
  const expected = [...expectedEntryTypes].sort();
  return eventually(`FIN journals of ${policyNumber}: ${expected.join(', ')}`, async () => {
    const all: Json[] = [];
    let cursor: string | undefined;
    do {
      const path = `/api/fin/v1/journals/query?policyNumber=${encodeURIComponent(policyNumber)}&limit=100${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`;
      const page = await call(request, token, 'GET', path);
      expect(page.status, page.text).toBe(200);
      all.push(...(page.body['items'] as Json[]).map((i) => i['journal'] as Json));
      cursor = page.body['nextCursor'] ?? undefined;
    } while (cursor);
    const actual = all.map((j) => `${j['sourceModule']}:${j['sourceEventType']}`).sort();
    return JSON.stringify(actual) === JSON.stringify(expected) ? all : undefined;
  });
}
