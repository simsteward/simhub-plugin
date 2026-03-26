import { test, expect } from '@playwright/test';
import path from 'path';

const dashDir = path.resolve(__dirname, '../../src/SimSteward.Dashboard');

function fileUrl(filename: string) {
  return `file:///${dashDir.replace(/\\/g, '/')}/${filename}`;
}

test.describe('data-capture-suite.html', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto(fileUrl('data-capture-suite.html'));
  });

  test('all steps present', async ({ page }) => {
    for (let i = 1; i <= 6; i++) {
      await expect(page.locator(`#step-${i}`)).toBeAttached();
    }
  });

  test('action buttons present', async ({ page }) => {
    for (const id of ['#btn-scope-full', '#btn-scope-partial', '#btn-preflight', '#btn-start', '#btn-cancel', '#btn-verify']) {
      await expect(page.locator(id)).toBeAttached();
    }
  });

  test('ws-pill indicator present', async ({ page }) => {
    // State (.ok/.bad) depends on whether SimHub is running — just assert element exists
    await expect(page.locator('#ws-pill')).toBeVisible();
  });
});

test.describe('index.html', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto(fileUrl('index.html'));
  });

  test('all tab buttons present', async ({ page }) => {
    for (const tab of ['events', 'health', 'telem', 'leaderboard', 'captured']) {
      await expect(page.locator(`[data-tab="${tab}"]`)).toBeAttached();
    }
  });

  test('auto-scroll button present', async ({ page }) => {
    await expect(page.locator('#asbtn')).toBeAttached();
  });

  test('ws-badge indicator present', async ({ page }) => {
    // State (.connected/.disconnected) depends on whether SimHub is running — just assert element exists
    await expect(page.locator('.ws-badge')).toBeVisible();
  });
});

test.describe('replay-incident-index.html', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto(fileUrl('replay-incident-index.html'));
  });

  test('action buttons present', async ({ page }) => {
    for (const id of ['#btn-start', '#btn-cancel', '#btn-record']) {
      await expect(page.locator(id)).toBeAttached();
    }
  });

  test('ws-pill shows disconnected', async ({ page }) => {
    await expect(page.locator('.ws-pill')).toBeAttached();
    await expect(page.locator('.ws-pill.bad')).toBeVisible({ timeout: 3_000 });
  });
});
