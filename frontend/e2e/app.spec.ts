import { expect, test } from '@playwright/test';

async function login(page: import('@playwright/test').Page) {
  await page.goto('/login');
  await page.getByLabel('Email').fill('admin@demo.com');
  await page.getByLabel('Password').fill('Admin@123');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.locator('.user-chip .role')).toHaveText('SuperAdmin'); // name is hidden on phones
}

test('unauthenticated user is redirected to login', async ({ page }) => {
  await page.goto('/');
  await expect(page).toHaveURL(/\/login$/);
});

test('dashboard shows seeded stats', async ({ page }) => {
  await login(page);
  await expect(page.locator('.stat', { hasText: 'Customers' })).toContainText('200');
});

// Needs a real GROQ_API_KEY in the stack; skipped otherwise.
test('chatbot answers a natural-language question from the database', async ({ page }) => {
  test.skip(!process.env.E2E_WITH_LLM, 'set E2E_WITH_LLM=1 to run against the live Groq API');
  await login(page);
  await page.getByRole('link', { name: 'AI Assistant' }).click();
  await page.getByLabel('question').fill('How many customers do I have?');
  await page.getByRole('button', { name: 'Ask' }).click();
  const reply = page.getByTestId('msg-assistant').last();
  await expect(reply).toContainText('200', { timeout: 45_000 });
  await expect(reply.getByText('SQL')).toBeVisible();
});

test('SuperAdmin sees the Master menu and can open user rights', async ({ page }) => {
  await login(page);
  const nav = page.getByRole('navigation', { name: 'Main menu' });
  await nav.getByRole('button', { name: 'Master' }).click();
  await nav.getByRole('button', { name: 'Users' }).click();
  await nav.getByRole('link', { name: 'Users', exact: true }).click();
  await expect(page.getByRole('table', { name: 'Users' })).toContainText('admin@demo.com');
  await nav.getByRole('link', { name: 'User Rights' }).click();
  await expect(page.getByRole('heading', { name: 'User rights' })).toBeVisible();
});

test.describe('mobile', () => {
  test.use({ viewport: { width: 390, height: 844 } });

  test('side menu lives behind the hamburger button', async ({ page }) => {
    await login(page);
    const nav = page.getByRole('navigation', { name: 'Main menu' });
    await expect(nav).not.toBeInViewport();
    await page.getByRole('button', { name: 'Open menu' }).click();
    await expect(nav).toBeInViewport();
    await nav.getByRole('button', { name: 'Inventory' }).click();
    await nav.getByRole('button', { name: 'Stock Position' }).click();
    await nav.getByRole('link', { name: /Stock Position/ }).click();
    await expect(page.getByRole('heading', { name: 'Coming soon' })).toBeVisible();
    await expect(nav).not.toBeInViewport(); // drawer closes after navigating
  });
});
