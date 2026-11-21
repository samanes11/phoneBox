const { test, expect } = require('@playwright/test');

test('loading the demo catalog does not overwrite a layout saved while it loads', async ({ page }) => {
  let catalogRequested;
  let releaseCatalog;
  const requested = new Promise(resolve => { catalogRequested = resolve; });
  const release = new Promise(resolve => { releaseCatalog = resolve; });
  await page.route('**/templates.json', async route => {
    catalogRequested();
    await release;
    await route.continue();
  });
  await page.goto('/panel/', { waitUntil: 'domcontentloaded' });
  await page.evaluate(() => {
    window.demoSettings = fetch('/api/settings').then(response => response.json());
  });
  await requested;
  await page.evaluate(() => localStorage.setItem('pb-demo-config', JSON.stringify({
    pages: [{ id: 'saved', title: 'Saved layout', widgets: [] }],
  })));
  releaseCatalog();
  const loaded = await page.evaluate(() => window.demoSettings);
  expect(loaded.pages.map(page => page.id)).toEqual(['saved']);
  expect(await page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config')).pages[0].id)).toBe('saved');
});
