import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './tests/playwright',
  snapshotDir: './tests/playwright/snapshots',
  use: { headless: true },
  projects: [
    {
      name: 'grafana',
      // Requires: pnpm obs:up
      use: { baseURL: 'http://localhost:3000', ...devices['Desktop Chrome'] },
      testMatch: '**/grafana-*.spec.ts',
    },
    {
      name: 'dashboard',
      // No deps — opens file:// URLs
      use: { ...devices['Desktop Chrome'] },
      testMatch: '**/dashboard-*.spec.ts',
    },
  ],
});
