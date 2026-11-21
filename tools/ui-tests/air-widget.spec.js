const { test, expect } = require('@playwright/test');

for (const [aqi, value, band] of [[null, '—', 'Unavailable'], [0, '0', 'Good']]) {
  test(`air quality displays the right reading for AQI ${aqi}`, async ({ page }) => {
    await page.addInitScript(() => {
      const config = JSON.stringify({
        night: { enabled: false }, rules: [], shortcuts: [],
        pages: [{ id: 'air-test', title: 'Air quality', widgets: [{ id: 'air', type: 'air', x: 0, y: 0, w: 12, h: 6 }] }],
      });
      localStorage.setItem('cfg', config);
      localStorage.setItem('pb-demo-config', config);
    });
    await page.route('https://air-quality-api.open-meteo.com/**', route => route.fulfill({
      json: { current: { european_aqi: aqi, pm2_5: 8 } },
    }));
    await page.goto('/panel/');
    const widget = page.locator('[data-id="air-test"] .w-air');
    await expect(widget.locator('.aq-chips')).toContainText('PM2.5 8');
    await expect(widget.locator('.aq-v')).toHaveText(value);
    await expect(widget.locator('.aq-c')).toContainText(band);
  });
}
