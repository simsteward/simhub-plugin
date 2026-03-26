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

// Find and screenshot each key panel by title
const panels = ['Hook Type Breakdown', 'Top Tools Used', 'Agent Events'];
for (const title of panels) {
  const header = page.locator(`[data-testid="data-testid Panel header ${title}"]`);
  const box = await header.boundingBox().catch(() => null);
  if (box) {
    // Get the parent panel container
    const panel = header.locator('xpath=ancestor::*[contains(@class,"react-grid-item")][1]');
    const panelBox = await panel.boundingBox().catch(() => box);
    await page.screenshot({
      path: `tmp/panel-${title.replace(/\s+/g, '-').toLowerCase()}.png`,
      clip: panelBox,
    });
    console.log(`✅ ${title} screenshot saved`);
  } else {
    console.log(`⚠️ Could not find panel: ${title}`);
  }
}

await browser.close();
