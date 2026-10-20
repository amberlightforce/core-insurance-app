import { expect, type APIRequestContext } from '@playwright/test';
import { call, eventually, signIn, type Json } from './api.js';

// Slice 3 helpers (SL3-E2E-HARNESS): the time-shifted environment and complete-set journal waits.

/** Advances the shiftable dev clock (POST /api/plt/v1/dev/clock/advance, Platform.Admin, forward-only; D-SL3-12). */
export async function advanceClock(request: APIRequestContext, advance: { days?: number; hours?: number; minutes?: number; seconds?: number }): Promise<Json> {
  const admin = await signIn(request, 'admin');
  const result = await call(request, admin, 'POST', '/api/plt/v1/dev/clock/advance', { days: advance.days ?? 0, hours: advance.hours ?? 0, minutes: advance.minutes ?? 0, seconds: advance.seconds ?? 0 });
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

/** The dev clock's current time (the server's "now"; never the runner's wall clock). */
export async function serverClockNow(request: APIRequestContext): Promise<Date> {
  return new Date(String((await readClock(request))['now']));
}

/**
 * Moves the dev clock forward (never waits on the runner's wall clock) until the server's now is at least `instant`.
 * Returns the server's now afterwards. A no-op when the instant is already in the past.
 */
export async function advanceServerPast(request: APIRequestContext, instant: Date): Promise<Date> {
  const now = await serverClockNow(request);
  const missingMs = instant.getTime() - now.getTime();
  if (missingMs > 0) await advanceClock(request, { seconds: Math.ceil(missingMs / 1000) });
  const after = await serverClockNow(request);
  expect(after.getTime(), 'the dev clock is past the requested instant').toBeGreaterThanOrEqual(instant.getTime());
  return after;
}
