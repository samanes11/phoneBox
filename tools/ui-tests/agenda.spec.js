const { test, expect } = require('@playwright/test');

test.use({ timezoneId: 'America/New_York' });

test('a one-day agenda ends at local midnight when clocks spring forward', async ({ page }) => {
  await page.clock.setFixedTime(new Date('2026-03-09T01:00:00Z')); // Sunday 21:00 after the change
  await page.addInitScript(() => {
    const config = JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      pages: [{ id: 'agenda-test', title: 'Agenda', widgets: [
        { id: 'agenda', type: 'agenda', x: 0, y: 0, w: 12, h: 6, options: { days: 1 } },
      ] }],
    });
    localStorage.setItem('cfg', config);
    localStorage.setItem('pb-demo-config', config);
  });
  await page.goto('/panel/');
  const agenda = page.locator('[data-id="agenda-test"] .w-agenda');
  await expect(agenda).toBeVisible();
  await page.evaluate(async () => {
    const { D, emit } = await import('/panel/main.js');
    const event = (title, start) => ({
      title, start: Date.parse(start), end: Date.parse(start) + 30 * 60_000, calendar: 'Work',
    });
    D.calendar = { updated: Date.now(), events: [
      event('Late Sunday', '2026-03-09T03:00:00Z'),
      event('Early Monday', '2026-03-09T04:30:00Z'),
    ] };
    emit('calendar');
  });

  await expect(agenda).toContainText('Late Sunday');
  await expect(agenda).not.toContainText('Early Monday');
});

test.describe('where the clocks change at midnight', () => {
  test.use({ timezoneId: 'America/Santiago' });

  test('a one-day agenda ends at midnight on the day whose own midnight was skipped', async ({ page }) => {
    // Sunday 6 September 2026 starts at 01:00 in Santiago; it is 21:00 that Sunday, and the window ends at Monday 00:00
    await page.clock.setFixedTime(new Date('2026-09-07T00:00:00Z'));
    await page.addInitScript(() => {
      const config = JSON.stringify({
        night: { enabled: false }, rules: [], shortcuts: [],
        pages: [{ id: 'agenda-test', title: 'Agenda', widgets: [
          { id: 'agenda', type: 'agenda', x: 0, y: 0, w: 12, h: 6, options: { days: 1 } },
        ] }],
      });
      localStorage.setItem('cfg', config);
      localStorage.setItem('pb-demo-config', config);
    });
    await page.goto('/panel/');
    const agenda = page.locator('[data-id="agenda-test"] .w-agenda');
    await expect(agenda).toBeVisible();
    await page.evaluate(async () => {
      const { D, emit } = await import('/panel/main.js');
      const event = (title, start) => ({
        title, start: Date.parse(start), end: Date.parse(start) + 30 * 60_000, calendar: 'Work',
      });
      D.calendar = { updated: Date.now(), events: [
        event('Late Sunday', '2026-09-07T02:00:00Z'),
        event('Early Monday', '2026-09-07T03:30:00Z'),
      ] };
      emit('calendar');
    });

    await expect(agenda).toContainText('Late Sunday');
    await expect(agenda).not.toContainText('Early Monday');
  });
});
