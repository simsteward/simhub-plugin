import { test, expect } from '@playwright/test';

const DASHBOARDS = [
  { uid: 'simsteward-deploy-health', name: 'Sim Steward Deploy Health' },
  { uid: 'claude-code-overview',     name: 'Claude Code Overview' },
  { uid: 'contextstream-deep-dive',  name: 'ContextStream Deep Dive' },
];

test.beforeEach(async ({ page }) => {
  const res = await page.request.post('/api/login', {
    data: {
      user: 'playwright-tests',
      password: process.env.PLAYWRIGHT_SERVICE_ACCOUNT_PW,
    },
  });
  if (!res.ok()) {
    throw new Error(
      `Grafana login failed (${res.status()}). Run "pnpm test:pw:setup" to create the playwright-tests user first.`
    );
  }
});

for (const { uid, name } of DASHBOARDS) {
  test(`${name} renders correctly`, async ({ page }) => {
    await page.goto(`/d/${uid}?orgId=1&kiosk`, { waitUntil: 'networkidle' });

    // Grafana 11 renders panels inside .react-grid-item wrappers
    await page.locator('.react-grid-item').first().waitFor({ state: 'visible', timeout: 20_000 });

    // Wait for loading bars to clear
    await page.waitForFunction(
      () => document.querySelectorAll('[data-testid="panel-loading-bar"]').length === 0,
      { timeout: 20_000 }
    );

    await expect(page).toHaveScreenshot(`${uid}.png`, {
      fullPage: true,
      maxDiffPixels: 50,
    });
  });
}
