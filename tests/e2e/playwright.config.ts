import { defineConfig } from '@playwright/test';

// Runs against a running stack: local Compose (default http://localhost:5000) or an Azure URL after deployment
// (INFRASTRUCTURE §4 rule 9). Override with E2E_BASE_URL.
export default defineConfig({
  testDir: './tests',
  forbidOnly: !!process.env['CI'],
  retries: process.env['CI'] ? 2 : 0,
  reporter: process.env['CI'] ? [['github'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: process.env['E2E_BASE_URL'] ?? 'http://localhost:5000',
    locale: 'el-GR',
    timezoneId: 'Europe/Athens',
    trace: 'on-first-retry',
  },
});
