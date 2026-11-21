const { test, expect } = require('@playwright/test');

// App console: the agent (here the demo) names the profile of the focused app; the phone sends ids and slots only.
async function layout(page, options) {
  await page.goto('/panel/');
  await page.evaluate((options) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, shortcuts: [],
      pages: [{ id: 'test', title: 'Test', enabled: true, widgets: [{ id: 'ac', type: 'app-console', x: 0, y: 0, w: 12, h: 6, options }] }] }));
  }, options);
  await page.reload();
  await expect(page.locator('.w.ac')).toBeVisible();
  // record what the widget posts, on top of the demo's own fetch (it answers in-page, so page.route doesn't see it)
  await page.evaluate(() => {
    window.__posts = [];
    const demo = window.fetch;
    window.fetch = (url, init) => {
      if (init && init.method === 'POST' && String(url).includes('/console/')) window.__posts.push(String(url).replace(/^.*\/console\//, ''));
      return demo(url, init);
    };
  });
}

test('app console shows the focused app\'s keys and sends ids, not keys', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await layout(page, { control: true });
  await expect(page.locator('.ac-title')).toHaveText('Photoshop');
  await expect(page.locator('.ac-key')).toHaveCount(8);
  await page.getByRole('button', { name: 'Undo' }).click();
  await expect.poll(() => page.evaluate(() => window.__posts)).toEqual(['photoshop/k2?widget=test%2Fac']);

  // a turn of the dial: 23 moves make 5 clicks, sent at most one request per click (fewer while one is in flight)
  const box = await page.locator('.ac-dial').boundingBox();
  const cx = box.x + box.width / 2, cy = box.y + box.height / 2, r = box.width / 2 - 6;
  await page.mouse.move(cx + r, cy);
  await page.mouse.down();
  // 5 clicks of 24°, with room for rounding before a 6th
  for (let deg = 0; deg <= 132; deg += 6) await page.mouse.move(cx + r * Math.cos(deg * Math.PI / 180), cy + r * Math.sin(deg * Math.PI / 180));
  await page.mouse.up();
  await expect.poll(() => page.evaluate(() => window.__posts.slice(1).reduce((n, p) => n + +(p.match(/times=(\d+)/) || [0, 0])[1], 0))).toBe(5);
  const turns = await page.evaluate(() => window.__posts.slice(1));
  expect(turns.length).toBeLessThanOrEqual(5);
  expect(turns.every((p) => p.startsWith('photoshop/dial-right?times=') && p.endsWith('&widget=test%2Fac'))).toBe(true);

  // a tap on the knob presses it
  await page.mouse.click(cx, cy);
  await expect.poll(() => page.evaluate(() => window.__posts.at(-1))).toBe('photoshop/dial-press?widget=test%2Fac');
  expect(errors).toEqual([]);
});

test('app console stays read-only until the widget allows sending keys', async ({ page }) => {
  await layout(page, {});
  await expect(page.locator('.ac-note')).toContainText('Send keys to the computer');
  await expect(page.locator('.ac-key').first()).toBeDisabled();
  await expect(page.locator('.ac-dial')).toBeHidden();
});
