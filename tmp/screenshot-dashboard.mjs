import { chromium } from '@playwright/test';

const browser = await chromium.launch({ headless: true });
const ctx = await browser.newContext({
  baseURL: 'http://localhost:3000',
  viewport: { width: 1920, height: 2400 },
});
const page = await ctx.newPage();

const authHeader = 'Basic ' + Buffer.from('admin:SimStewardLocalDev!').toString('base64');
await page.setExtraHTTPHeaders({ Authorization: authHeader });

await page.goto('/d/claude-code-overview?orgId=1&from=now-6h&to=now&kiosk', {
  waitUntil: 'networkidle',
  timeout: 30_000,
});
console.log('At:', page.url());

await page.locator('.react-grid-item').first().waitFor({ state: 'visible', timeout: 30_000 });
await page.waitForFunction(
  () => document.querySelectorAll('[data-testid="panel-loading-bar"]').length === 0,
  { timeout: 30_000 }
).catch(() => console.log('Loading bars timeout - proceeding'));

await page.waitForTimeout(5000);

await page.screenshot({ path: 'tmp/dashboard-current.png', fullPage: true });
console.log('✅ Screenshot saved: tmp/dashboard-current.png');

await browser.close();
