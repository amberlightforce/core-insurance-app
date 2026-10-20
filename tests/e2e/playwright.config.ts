import { defineConfig } from '@playwright/test';

// Runs against a running stack: local Compose (default http://localhost:5000) or an Azure URL after deployment
// (INFRASTRUCTURE §4 rule 9). Override with E2E_BASE_URL.
export default defineConfig({
  testDir: './tests',
  forbidOnly: !!process.env['CI'],
  // One retry in CI: enough to absorb an infrastructure blip, few enough that flakiness stays visible (see flaky-reporter).
  retries: process.env['CI'] ? 1 : 0,
  reporter: process.env['CI']
    ? [['github'], ['./reporters/flaky-reporter.ts'], ['html', { open: 'never' }]]
    : 'list',
  use: {
    baseURL: process.env['E2E_BASE_URL'] ?? 'http://localhost:5000',
    locale: 'el-GR',
    timezoneId: 'Europe/Athens',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
  },
});
