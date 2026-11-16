import { expect, test, type Page } from '@playwright/test';

/** One journey per screen against the real stack (demo data). Anything a test creates, it deletes again. */
const ADMIN = { email: 'admin@demo.com', password: 'Admin@123', role: 'SuperAdmin' };
const STAFF = { email: 'staff@demo.com', password: 'Staff@1234', role: 'User' };

async function login(page: Page, who = ADMIN) {
  await page.goto('/login');
  await page.getByLabel('Email').fill(who.email);
  await page.getByLabel('Password').fill(who.password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.locator('.user-chip .role')).toHaveText(who.role);
}

const nav = (page: Page) => page.getByRole('navigation', { name: 'Main menu' });

test.describe('screens (SuperAdmin)', () => {
  test.beforeEach(async ({ page }) => login(page));

  test('Dashboard: totals and the Sales analytics section', async ({ page }) => {
    await expect(page.locator('.stat', { hasText: 'Total orders' })).toContainText(/\d/);
    const section = page.getByRole('region', { name: 'Sales analytics' });
    await expect(section.getByRole('img', { name: /Revenue per day/ })).toBeVisible();
    await section.getByRole('link', { name: /Open full analytics/ }).click();
    await expect(page).toHaveURL(/\/analytics$/);
  });

  test('Analytics: KPIs, chart tooltip, range switch, data table', async ({ page }) => {
    await nav(page).getByRole('link', { name: 'Analytics' }).click();
    await expect(page.getByRole('heading', { level: 1, name: 'Analytics' })).toBeVisible();
    await expect(page.getByRole('region', { name: 'Key figures' })).toContainText('Revenue');

    const chart = page.getByRole('img', { name: /Revenue per day/ });
    const box = (await chart.boundingBox())!;
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await expect(page.locator('.tooltip')).toContainText('₹');

    await page.getByRole('button', { name: '12 months' }).click();
    await expect(page.getByRole('img', { name: /Revenue per month/ })).toBeVisible();
    await page.getByText('Show data table').click();
    await expect(page.locator('.data-table tbody tr')).toHaveCount(12);
    await expect(page.getByRole('heading', { name: 'Revenue by city' })).toBeVisible();
  });

  test('Customers: seeded list is shown', async ({ page }) => {
    await page.goto('/master/customers');
    await expect(page.getByRole('table', { name: 'Customers' }).locator('tbody tr')).toHaveCount(25);
  });

  test('Products: seeded list with prices', async ({ page }) => {
    await page.goto('/master/products');
    await expect(page.getByRole('table', { name: 'Products' })).toContainText('₹');
  });

  test('Sales Orders: latest orders', async ({ page }) => {
    await nav(page).getByRole('button', { name: 'Sales & Labour Work' }).click();
    await nav(page).getByRole('button', { name: 'Orders' }).click();
    await nav(page).getByRole('link', { name: 'Sales Orders' }).click();
    await expect(page.getByRole('table', { name: 'Orders' }).locator('tbody tr')).toHaveCount(25);
  });

  test('Users: create a user, then delete it', async ({ page }) => {
    const email = `e2e-${Date.now()}@demo.com`;
    await page.goto('/master/users');
    const form = page.getByRole('form', { name: 'Create user' });
    await form.getByLabel('Full name').fill('E2E Temp');
    await form.getByLabel('Email').fill(email);
    await form.getByLabel('Password').fill('Passw0rd!');
    await form.getByRole('button', { name: 'Create user' }).click();
    const row = page.getByRole('row', { name: new RegExp(email) });
    await expect(row).toBeVisible();

    page.once('dialog', (d) => d.accept());
    await row.getByRole('button', { name: 'Delete' }).click();
    await expect(row).toHaveCount(0);
  });

  test('User Rights: pick a user and see their matrix', async ({ page }) => {
    await page.goto('/master/user-rights');
    const picker = page.getByRole('combobox', { name: 'User' });
    const staff = picker.locator('option', { hasText: '(User)' }).first();
    await picker.selectOption((await staff.getAttribute('value'))!);
    await expect(page).toHaveURL(/user=/);
    await expect(page.getByRole('checkbox').first()).toBeVisible();
  });

  test('Role Rights: User role defaults are listed', async ({ page }) => {
    await page.goto('/master/role-rights');
    await expect(page.getByRole('combobox', { name: 'Role' })).toHaveValue('2');
    await expect(page.getByRole('row', { name: /Customers/ }).first()).toBeVisible();
  });

  test('Pages master: Analytics is listed as Ready', async ({ page }) => {
    await page.goto('/master/pages');
    await expect(page.getByRole('row', { name: /analytics/ })).toContainText('Ready');
  });

  test('legacy V.SMART URL redirects', async ({ page }) => {
    await page.goto('/customer');
    await expect(page).toHaveURL(/\/master\/customers$/);
  });
});

test.describe('screens (User role)', () => {
  test.beforeEach(async ({ page }) => login(page, STAFF));

  test('no Analytics in menu or on the Dashboard, and the URL is blocked', async ({ page }) => {
    await expect(page.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeVisible();
    await expect(nav(page).getByRole('link', { name: 'Analytics' })).toHaveCount(0);
    await expect(page.getByRole('region', { name: 'Sales analytics' })).toHaveCount(0);
    await page.goto('/analytics');
    await expect(page.getByRole('heading', { name: 'No access' })).toBeVisible();
  });

  test('can read Customers but sees no Delete button', async ({ page }) => {
    await page.goto('/master/customers');
    await expect(page.getByRole('table', { name: 'Customers' }).locator('tbody tr').first()).toBeVisible();
    await expect(page.getByRole('button', { name: 'Delete' })).toHaveCount(0);
  });

  test('Master screens are blocked', async ({ page }) => {
    await page.goto('/master/users');
    await expect(page.getByRole('heading', { name: 'No access' })).toBeVisible();
  });
});
