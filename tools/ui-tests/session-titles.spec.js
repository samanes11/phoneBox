const { test, expect } = require('@playwright/test');

async function layout(page, widgets, extra = {}) {
  await page.goto('/panel/style.css');
  await page.evaluate(({ widgets, extra }) => {
    localStorage.clear();
    // Widget interaction tests must not depend on the host's wall-clock night schedule.
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, ...extra, pages: [{ id: 'test', title: 'Test', enabled: true, widgets }], shortcuts: [] }));
  }, { widgets, extra });
  await page.goto('/panel/');
  await expect(page.locator('.w').first()).toBeVisible();
  await expect(page.locator('body')).not.toHaveClass(/night/);
}
const widget = (type, options = {}) => ({ id: 'test', type, x: 0, y: 0, w: 12, h: 6, options });

const LONG = 'A very long chat title that goes on and on about invoices, customers, reminders and many other things '.repeat(2).trim();
const session = (over) => ({ id: 'claude:t1', tool: 'claude', name: 'develop-int-61', project: 'develop-int', status: 'idle', since: Date.now() - 60_000,
  startedAt: Date.now() - 3600_000, model: null, effort: null, totalTokens: null, inputTokens: null, outputTokens: null, cachedInputTokens: null,
  reasoningTokens: null, lastInputTokens: null, contextWindow: null, lastTool: null, turnStartedAt: null, outcome: null, ...over });

async function show(page, sessions) {
  await layout(page, [widget('sessions')]);
  await page.evaluate(async (sessions) => {
    const { D, emit } = await import('/panel/main.js');
    D.work = { sessions, github: { user: null, notifications: 0, reviewRequests: [], myPrs: [], error: null } };
    emit('work');
  }, sessions);
}

test('without a title the row shows the folder, as before', async ({ page }) => {
  await show(page, [session({})]);
  const row = page.locator('button.sess');
  await expect(row.locator('.nm')).toHaveText('develop-int');
  await expect(row.locator('.sess-meta')).not.toContainText('develop-int');
});

test('a chat title is the main line with the folder under it, and a long one is cut with an ellipsis', async ({ page }) => {
  await show(page, [session({ title: 'Quarterly invoice numbers' }), session({ id: 'claude:t2', title: LONG })]);
  const rows = page.locator('button.sess');
  await expect(rows).toHaveCount(2);
  const first = rows.filter({ hasText: 'Quarterly invoice numbers' });
  await expect(first.locator('.nm')).toHaveText('Quarterly invoice numbers');
  await expect(first.locator('.sess-meta')).toContainText('develop-int');
  const long = rows.filter({ hasText: 'A very long chat title' });
  const nm = long.locator('.nm');
  const cut = await nm.evaluate((e) => ({ over: e.scrollWidth > e.clientWidth, ellipsis: getComputedStyle(e).textOverflow, wrap: getComputedStyle(e).whiteSpace }));
  expect(cut).toEqual({ over: true, ellipsis: 'ellipsis', wrap: 'nowrap' });
  const rowBox = await long.boundingBox();
  expect(rowBox.height).toBeLessThan(60); // the long title did not wrap the row
});

test('the details dialog shows its facts in one compact row and cuts a long title', async ({ page }) => {
  await page.setViewportSize({ width: 600, height: 520 });
  await show(page, [session({ title: LONG })]);
  await page.locator('button.sess').click();
  const dialog = page.getByRole('dialog');
  const cells = dialog.locator('.session-cell');
  await expect(cells).toHaveCount(4);
  await expect(cells.nth(0)).toContainText('Folder');
  await expect(cells.nth(0)).toContainText('develop-int');
  const tops = await cells.evaluateAll((els) => new Set(els.map((e) => Math.round(e.getBoundingClientRect().top))).size);
  expect(tops).toBe(1);
  const h2 = await dialog.locator('h2').evaluate((e) => ({ clamp: getComputedStyle(e).webkitLineClamp, height: e.getBoundingClientRect().height }));
  expect(h2.clamp).toBe('2');
  expect(h2.height).toBeLessThan(70);
});
