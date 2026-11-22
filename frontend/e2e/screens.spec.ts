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
    // Reports > Analytics > Analytics in the side menu (the URL is still /analytics)
    await nav(page).getByRole('button', { name: 'Reports' }).click();
    await nav(page).getByRole('button', { name: 'Analytics' }).click();
    await nav(page).getByRole('link', { name: 'Analytics' }).click();
    await expect(page).toHaveURL(/\/analytics$/);
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

  test('Pages master (Settings › Pages): Analytics is listed as Ready; not in the side menu', async ({ page }) => {
    await expect(nav(page).getByRole('link', { name: 'Pages' })).toHaveCount(0);
    await page.goto('/master/pages'); // old URL redirects to the Settings tab
    await expect(page).toHaveURL(/\/settings\/pages$/);
    await expect(page.getByRole('row', { name: /analytics/ })).toContainText('Ready');
  });

  test('legacy V.SMART URL redirects', async ({ page }) => {
    await page.goto('/customer');
    await expect(page).toHaveURL(/\/master\/customers$/);
  });

  test('Settings: profile, two-factor setup (cancelled) and the Users tab', async ({ page }) => {
    await page.getByRole('link', { name: 'Settings' }).click();
    await expect(page.getByRole('heading', { level: 1, name: 'Settings' })).toBeVisible();
    await expect(page.getByLabel('Email')).toHaveValue(ADMIN.email);

    // Start the setup to see the QR code, then cancel: 2FA stays off, so other journeys can still sign in
    await page.getByRole('navigation', { name: 'Settings sections' }).getByRole('link', { name: 'Two-factor sign-in' }).click();
    await page.getByRole('button', { name: /set up two-factor/i }).click();
    await expect(page.getByRole('img', { name: /qr code/i })).toBeVisible();
    await expect(page.getByLabel('Setup key')).not.toBeEmpty();
    await page.getByRole('button', { name: 'Cancel' }).click();
    await expect(page.getByRole('button', { name: /set up two-factor/i })).toBeVisible();

    // SuperAdmin edits everyone below: the demo Admin, Manager and User are listed, the owner is not
    await page.getByRole('navigation', { name: 'Settings sections' }).getByRole('link', { name: 'Users' }).click();
    const table = page.getByRole('table', { name: 'Editable users' });
    await expect(table.getByText('manager@demo.com')).toBeVisible();
    await expect(table.getByText('staff@demo.com')).toBeVisible();
    await expect(table.getByText(ADMIN.email)).toHaveCount(0);
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

  test('Settings: own account only, no Users tab', async ({ page }) => {
    await page.goto('/settings/users');
    await expect(page.getByRole('form', { name: 'Profile' })).toBeVisible(); // sent back to Profile
    const tabs = page.getByRole('navigation', { name: 'Settings sections' });
    await expect(tabs.getByRole('link', { name: 'Two-factor sign-in' })).toBeVisible();
    await expect(tabs.getByRole('link', { name: 'Users' })).toHaveCount(0);
  });
});

test.describe('Messages (end-to-end encrypted)', () => {
  const PASSPHRASE = 'e2e secure chat passphrase';

  /** Opens Messages and gets past the key gate whatever state an earlier run left the demo user in. */
  async function openMessages(page: Page) {
    await nav(page).getByRole('link', { name: 'Messages' }).click();
    const setup = page.getByRole('heading', { name: 'Set up secure messaging' });
    const unlock = page.getByRole('heading', { name: 'Unlock your messages' });
    await expect(setup.or(unlock)).toBeVisible();
    if (await setup.isVisible()) {
      await page.getByLabel('New passphrase').fill(PASSPHRASE);
      await page.getByLabel('Repeat passphrase').fill(PASSPHRASE);
      await page.getByRole('button', { name: 'Set up' }).click();
    } else {
      await page.getByLabel('Passphrase').fill(PASSPHRASE);
      await page.getByRole('button', { name: 'Unlock' }).click();
      if (await page.getByText('That passphrase is not correct.').isVisible({ timeout: 3000 }).catch(() => false)) {
        await page.getByRole('button', { name: 'Forgot your passphrase?' }).click();
        await page.getByLabel('New passphrase').fill(PASSPHRASE);
        await page.getByLabel('Repeat passphrase').fill(PASSPHRASE);
        await page.getByRole('button', { name: 'Reset key' }).click();
      }
    }
    await expect(page.getByRole('complementary', { name: 'Conversations' })).toBeVisible();
  }

  test('the SuperAdmin announces, a member decrypts it but cannot reply there or start a chat with senior roles', async ({ browser }) => {
    const staff = await (await browser.newContext()).newPage();
    await login(staff, STAFF);
    await openMessages(staff); // the member's key must exist before the announcement is encrypted for it

    const owner = await (await browser.newContext()).newPage();
    await login(owner);
    await openMessages(owner);
    await expect(owner.getByRole('button', { name: 'New' })).toBeVisible(); // the owner can start chats too
    await owner.getByRole('button', { name: /Announcements/ }).click();
    const text = `Plant closed on Friday (${Date.now()})`;
    await owner.getByLabel('Message').fill(text);
    await owner.getByRole('button', { name: 'Send' }).click();
    await expect(owner.getByTestId('chat-message').filter({ hasText: text })).toBeVisible();

    await staff.getByRole('button', { name: /Announcements/ }).click();
    await expect(staff.getByTestId('chat-message').filter({ hasText: text })).toBeVisible();
    await expect(staff.getByText('Only the organization owner can post here.')).toBeVisible();
    await expect(staff.getByLabel('Message')).toHaveCount(0);

    // A User has no "Message Senior Roles" right by default: more senior people aren't contacts
    await staff.getByRole('button', { name: 'New' }).click();
    await expect(staff.getByText(/listed only if you have the Message Senior Roles right/)).toBeVisible();
    for (const senior of [/Demo Admin/, /Demo Manager/, /Demo Team Lead/])
      await expect(staff.getByRole('checkbox', { name: senior })).toHaveCount(0);
  });

  test('everyone in the organization posts in the General channel', async ({ browser }) => {
    const staff = await (await browser.newContext()).newPage();
    await login(staff, STAFF);
    await openMessages(staff);

    const owner = await (await browser.newContext()).newPage();
    await login(owner);
    await openMessages(owner);
    await owner.getByRole('button', { name: /General/ }).click();
    const hello = `Good morning everyone (${Date.now()})`;
    await owner.getByLabel('Message').fill(hello);
    await owner.getByRole('button', { name: 'Send' }).click();
    await expect(owner.getByTestId('chat-message').filter({ hasText: hello })).toBeVisible();

    await staff.getByRole('button', { name: /General/ }).click();
    await expect(staff.getByTestId('chat-message').filter({ hasText: hello })).toBeVisible();
    const reply = `Morning! (${Date.now()})`;
    await staff.getByLabel('Message').fill(reply);
    await staff.getByRole('button', { name: 'Send' }).click();
    await expect(owner.getByTestId('chat-message').filter({ hasText: reply })).toBeVisible();
  });
});
