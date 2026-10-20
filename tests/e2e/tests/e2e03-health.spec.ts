import { expect, test } from '@playwright/test';
import { call, signIn } from './support/api.js';
import { advanceClock, readClock } from './support/time.js';

// Placeholder of the E2E-03 harness (SL3-E2E-HARNESS): the shiftable stack of tests/e2e/run-e2e03.sh is healthy, the dev clock
// is readable and advances one day. The journeys (E2E-03/04, change journeys) are added by SL3-E2E.

test('e2e03 stack is healthy and the dev clock advances by one day', async ({ request }) => {
  const health = await request.get('/health/ready');
  expect(health.status()).toBe(200);

  // The dev clock is mandatory here: a missing endpoint (404) is a failure, never a skip (NX-TST-02).
  const admin = await signIn(request, 'admin');
  const probe = await call(request, admin, 'GET', '/api/plt/v1/dev/clock');
  expect(probe.status, 'GET /api/plt/v1/dev/clock must exist on the shiftable E2E stack (Platform__Time__Mode=Shiftable)').toBe(200);

  const before = await readClock(request);
  const after = await advanceClock(request, { days: 1 });
  const now = (body: Record<string, unknown>): number => Date.parse(String(body['now']));
  expect(now(after) - now(before), 'now moved forward by about one day').toBeGreaterThanOrEqual(24 * 3600 * 1000 - 5000);
});
