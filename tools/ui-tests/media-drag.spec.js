const { test, expect } = require('@playwright/test');

test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => {
    const config = JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      pages: [{ id: 'media-test', title: 'Media', widgets: [
        { id: 'media', type: 'media', x: 0, y: 0, w: 12, h: 6 },
      ] }],
    });
    localStorage.setItem('cfg', config);
    localStorage.setItem('pb-demo-config', config);
  });
  await page.goto('/panel/');
});

test('a cancelled Media volume drag stops suppressing agent volume updates', async ({ page }) => {
  const media = page.locator('[data-id="media-test"] .w-media');
  await expect(media).toBeVisible();
  await page.evaluate(async () => {
    const main = await import('/panel/main.js');
    const volume = document.querySelector('[data-id="media-test"] .volr');
    volume.value = '81';
    volume.dispatchEvent(new Event('input', { bubbles: true }));
    volume.dispatchEvent(new PointerEvent('pointercancel', { bubbles: true, pointerType: 'touch' }));
    main.D.state = { ...main.D.state, audio: { volume: 42, canControl: true } };
    main.emit('state');
  });
  await expect(media.locator('.vol-val')).toHaveText('42');
  await expect(media.locator('.volr')).toHaveValue('42');
});

test('rebuilding Media during a volume drag releases the old slider', async ({ page }) => {
  const media = page.locator('[data-id="media-test"] .w-media');
  await expect(media).toBeVisible();
  await page.evaluate(async () => {
    const main = await import('/panel/main.js');
    const volume = document.querySelector('[data-id="media-test"] .volr');
    volume.value = '81';
    volume.dispatchEvent(new Event('input', { bubbles: true }));
    const config = JSON.parse(localStorage.getItem('pb-demo-config'));
    config.pages[0].title = 'Rebuilt Media';
    localStorage.setItem('pb-demo-config', JSON.stringify(config));
    await main.loadConfig();
    main.D.state = { ...main.D.state, audio: { volume: 42, canControl: true } };
    main.emit('state');
  });
  await expect(media.locator('.volr')).toHaveValue('42');
});
