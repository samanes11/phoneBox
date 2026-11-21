const { test, expect } = require('@playwright/test');

test('air quality clears the old reading and uses the newly saved weather location', async ({ page }) => {
  await page.addInitScript(() => {
    const config = JSON.stringify({
      weather: { city: 'First City', lat: 20, lon: 30 },
      night: { enabled: false }, rules: [], shortcuts: [],
      pages: [{ id: 'air-location', title: 'Air Quality', widgets: [
        { id: 'air', type: 'air', x: 0, y: 0, w: 12, h: 6 },
      ] }],
    });
    localStorage.setItem('cfg', config);
    localStorage.setItem('pb-demo-config', config);
  });
  let releaseNewReading;
  const newReading = new Promise(resolve => { releaseNewReading = resolve; });
  await page.route('https://air-quality-api.open-meteo.com/**', async (route) => {
    const newLocation = new URL(route.request().url()).searchParams.get('latitude') === '21';
    if (newLocation) await newReading;
    return route.fulfill({ json: { current: { european_aqi: newLocation ? 60 : 10 } } });
  });
  await page.goto('/panel/');
  const widget = page.locator('[data-id="air-location"] .w-air');
  await expect(widget.locator('.w-sub')).toHaveText('First City');
  await expect(widget.locator('.aq-v')).toHaveText('10');
  await page.evaluate(async () => {
    const config = JSON.parse(localStorage.getItem('pb-demo-config'));
    config.weather = { city: 'Second City', lat: 21, lon: 31 };
    localStorage.setItem('pb-demo-config', JSON.stringify(config));
    const main = await import('/panel/main.js');
    await main.loadConfig();
    main.emit('tick');
  });
  try {
    await expect(widget.locator('.w-sub')).toHaveText('Second City');
    await expect(widget.locator('.aq-v')).toHaveText('—');
  } finally {
    releaseNewReading();
  }
  await expect(widget.locator('.aq-v')).toHaveText('60');
});
