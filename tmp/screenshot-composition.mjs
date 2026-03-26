import { chromium } from '@playwright/test';

const browser = await chromium.launch({ headless: true });
const ctx = await browser.newContext({
  baseURL: 'http://localhost:3000',
  viewport: { width: 1920, height: 1080 },
});
const page = await ctx.newPage();

const authHeader = 'Basic ' + Buffer.from('admin:SimStewardLocalDev!').toString('base64');
await page.setExtraHTTPHeaders({ Authorization: authHeader });

await page.goto('/d/claude-code-overview?orgId=1&from=now-6h&to=now&kiosk', {
  waitUntil: 'networkidle',
  timeout: 30_000,
});

await page.locator('.react-grid-item').first().waitFor({ state: 'visible', timeout: 30_000 });
await page.waitForFunction(
  () => document.querySelectorAll('[data-testid="panel-loading-bar"]').length === 0,
  { timeout: 30_000 }
).catch(() => {});
await page.waitForTimeout(3000);

// Screenshot the Composition row specifically
const composition = await page.locator('[data-testid="data-testid Panel header Hook Type Breakdown"]').boundingBox()
  .catch(() => null);

if (composition) {
  await page.screenshot({
    path: 'tmp/composition-row.png',
    clip: { x: 0, y: composition.y - 30, width: 1920, height: 500 },
  });
  console.log('✅ Composition screenshot saved');
} else {
  // Fallback: scroll to composition area and screenshot
  await page.evaluate(() => window.scrollTo(0, 600));
  await page.waitForTimeout(500);
  await page.screenshot({ path: 'tmp/composition-row.png', clip: { x: 0, y: 0, width: 1920, height: 600 } });
  console.log('✅ Screenshot saved (fallback)');
}

// Also get agent insights
await page.evaluate(() => window.scrollTo(0, 1100));
await page.waitForTimeout(500);
await page.screenshot({ path: 'tmp/agent-insights.png', clip: { x: 0, y: 0, width: 1920, height: 600 } });
console.log('✅ Agent insights screenshot saved');

await browser.close();
