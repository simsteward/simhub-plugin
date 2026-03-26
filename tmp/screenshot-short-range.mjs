import { chromium } from '@playwright/test';

const browser = await chromium.launch({ headless: true });
const ctx = await browser.newContext({
  baseURL: 'http://localhost:3000',
  viewport: { width: 1920, height: 1200 },
});
const page = await ctx.newPage();
const authHeader = 'Basic ' + Buffer.from('admin:SimStewardLocalDev!').toString('base64');
await page.setExtraHTTPHeaders({ Authorization: authHeader });

// Use 30-minute range to test short-range scaling
await page.goto('/d/claude-code-overview?orgId=1&from=now-30m&to=now&kiosk', {
  waitUntil: 'networkidle', timeout: 30_000,
});
await page.locator('.react-grid-item').first().waitFor({ state: 'visible', timeout: 30_000 });
await page.waitForFunction(
  () => document.querySelectorAll('[data-testid="panel-loading-bar"]').length === 0,
  { timeout: 30_000 }
).catch(() => {});
await page.waitForTimeout(4000);

await page.screenshot({ path: 'tmp/short-range.png', fullPage: false });
console.log('✅ Short-range screenshot saved');
await browser.close();
