const { test, expect } = require('@playwright/test');

test('exchange shows a change only when both rates are available', async ({ page }) => {
  await page.addInitScript(() => {
    const config = JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      pages: [{ id: 'exchange-test', title: 'Exchange', widgets: [
        { id: 'rates', type: 'exchange', x: 0, y: 0, w: 12, h: 6, options: { symbols: ['USD', 'JPY', 'GBP'] } },
      ] }],
    });
    localStorage.setItem('cfg', config);
    localStorage.setItem('pb-demo-config', config);
  });
  await page.route('https://api.frankfurter.dev/**', route => route.fulfill({
    json: { rates: { '2026-11-12': { USD: 1.20 }, '2026-11-13': { USD: 1.26, JPY: 160 } } },
  }));
  await page.goto('/panel/');
  const rows = page.locator('[data-id="exchange-test"] .fx-i');
  await expect(rows).toHaveCount(3);
  await expect(rows.filter({ hasText: 'USD' }).locator('.chg')).toContainText('5.00%');
  await expect(rows.filter({ hasText: 'JPY' }).locator('.fx-v')).toHaveText('160.00');
  await expect(rows.filter({ hasText: 'JPY' }).locator('.chg')).toHaveText('—');
  await expect(rows.filter({ hasText: 'GBP' }).locator('.chg')).toHaveText('—');
});
