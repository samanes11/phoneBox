// OpenCode answers on the phone are off by default, and the switch has to keep what it is told: the settings page
// must bind the same key the settings API sends ("opencode"). A key the page never reads back turns itself on
// again — the page's own default wins the save, and the next save of any setting writes "true" over it.
const { test, expect } = require('@playwright/test');

const config = (opencode) => ({
  display: { language: 'en', theme: 'default' },
  pages: [],
  opencode: { timeoutSec: 90, onlyWhenAway: false, alwaysCountdownSec: 10, watchWsl: false, shareSessionTitles: false, ...opencode },
});

async function seed(page, cfg) {
  await page.goto('/panel/style.css');
  await page.evaluate((c) => localStorage.setItem('pb-demo-config', JSON.stringify(c)), cfg);
  await page.goto('/settings/#general');
}

test('the page shows the saved OpenCode answers, and turning them off survives a save and a reload', async ({ page }) => {
  await seed(page, config({ phoneApprovals: true }));
  const toggle = page.locator('[data-bind="opencode.phoneApprovals"]');
  await expect(toggle).toBeChecked(); // what the settings file says is what the page shows
  const switchRow = page.locator('label.switch', { has: toggle });
  await switchRow.click(); // the input itself is hidden behind the switch
  await expect(toggle).not.toBeChecked();
  await page.evaluate(() => {
    const original = window.fetch;
    window.__savedBodies = [];
    window.fetch = (url, options) => {
      if (String(url).includes('/api/settings') && options?.method === 'PUT') window.__savedBodies.push(options.body);
      return original(url, options);
    };
  });
  await page.locator('#save').click();
  await expect(page.locator('#toast')).toHaveText(/^Saved/);
  const bodies = await page.evaluate(() => window.__savedBodies);
  const saved = JSON.parse(bodies[0]);
  expect(saved.opencode.phoneApprovals, 'CONTROL: the switch was turned off. EXPECTED: saved as false')
    .toBe(false);                          // the key the API sends and the config file uses
  expect(saved.openCode).toBeUndefined(); // not the camelCase ghost of it

  await page.reload();
  await expect(page.locator('[data-bind="opencode.phoneApprovals"]')).not.toBeChecked();
});

test('a later save of an unrelated setting keeps the answers off', async ({ page }) => {
  await seed(page, config({ phoneApprovals: false }));
  await expect(page.locator('[data-bind="opencode.phoneApprovals"]')).not.toBeChecked();

  await page.locator('[data-bind="display.theme"]').selectOption('graphite');
  await page.locator('#save').click();
  await expect(page.locator('#toast')).toHaveText(/^Saved/);

  await page.reload();
  await expect(page.locator('[data-bind="display.theme"]')).toHaveValue('graphite');
  await expect(page.locator('[data-bind="opencode.phoneApprovals"]')).not.toBeChecked();
});
