import { expect, test } from '@playwright/test';

test('the api answers its liveness probe', async ({ request }) => {
  const response = await request.get('/health/live');

  expect(response.status()).toBe(200);
});
