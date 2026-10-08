import { expect, test } from '@playwright/test';
import { call, signIn } from './support/api.js';
import { advanceClock, readClock } from './support/time.js';

// Placeholder of the E2E-03 harness (SL3-E2E-HARNESS): the shiftable stack of tests/e2e/run-e2e03.sh is healthy, the dev clock
// is readable and advances one day. The journeys (E2E-03/04, change journeys) are added by SL3-E2E.

test('e2e03 stack is healthy and the dev clock advances by one day', async ({ request }) => {
  const health = await request.get('/health/ready');
  expect(health.status()).toBe(200);

  // Until SL3-PLT-SUPPORT (dev clock endpoint) is merged the endpoint answers 404: skip the clock step with that reason.
  // Remove this probe after merging main with SL3-PLT-SUPPORT.
  const admin = await signIn(request, 'admin');
  const probe = await call(request, admin, 'GET', '/api/plt/v1/dev/clock');
  test.skip(probe.status === 404, 'GET /dev/clock is not available yet (SL3-PLT-SUPPORT not merged)');

  const before = await readClock(request);
  const after = await advanceClock(request, { days: 1 });
  const now = (body: Record<string, unknown>): number => Date.parse(String(body['now']));
  expect(now(after) - now(before), 'now moved forward by about one day').toBeGreaterThanOrEqual(24 * 3600 * 1000 - 5000);
});
