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
  waitUntil: 'networkidle', timeout: 30_000,
});
await page.locator('.react-grid-item').first().waitFor({ state: 'visible', timeout: 30_000 });
await page.waitForFunction(
  () => document.querySelectorAll('[data-testid="panel-loading-bar"]').length === 0,
  { timeout: 30_000 }
).catch(() => {});
await page.waitForTimeout(5000);

// Screenshot the Cross-Session row
const crossHeader = page.locator('text=Cross-Session Trends').first();
const box = await crossHeader.boundingBox().catch(() => null);
if (box) {
  await page.screenshot({
    path: 'tmp/cross-session.png',
    clip: { x: 0, y: box.y - 10, width: 1920, height: 400 },
  });
  console.log('✅ Cross-session screenshot at y=' + box.y);
} else {
  console.log('Could not find Cross-Session header');
}
await browser.close();
