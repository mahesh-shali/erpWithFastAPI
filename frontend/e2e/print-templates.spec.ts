import { expect, test, type Page } from '@playwright/test';

/**
 * Custom print templates against the real stack: create from the starter, preview, use for invoices, print a draft
 * invoice with it, then switch back to the built-in layout and clean up (template archived, draft deleted).
 */
async function login(page: Page, email = 'admin@demo.com', password = 'Admin@123', role = 'SuperAdmin') {
  await page.goto('/login');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.locator('.user-chip .role')).toHaveText(role);
}

const accept = (page: Page) => page.once('dialog', (d) => d.accept());

test('own invoice layout: starter → preview → default → print → back to built-in', async ({ page }) => {
  const marker = `E2E-LAYOUT-${Date.now().toString().slice(-6)}`;
  await login(page);

  // 1. Settings › Print templates: new template from the starter, with a marker and a hostile script
  await page.goto('/settings/print-templates');
  await page.getByRole('button', { name: 'New template' }).click();
  const form = page.getByRole('form', { name: 'Print template' });
  await form.getByLabel('Name').fill(marker);
  const editor = form.getByLabel('Template');
  await expect(editor).toHaveValue(/invoice\.number/);
  const starter = await editor.inputValue();
  await editor.fill(`<p class="marker">${marker} {{ invoice.number }}</p><script>document.title = "pwned"</script>\n${starter}`);
  // The live preview next to the editor follows the edit by itself
  const preview = page.getByRole('region', { name: 'Live preview' }).getByRole('document', { name: 'Template preview' });
  await expect(preview.locator('.marker')).toContainText(`${marker} SI-00042`);   // locators pierce the shadow root
  await expect(preview.locator('script')).toHaveCount(0);
  await form.getByRole('button', { name: 'Save and use for invoices' }).click();
  await expect(page.getByText('Template saved.')).toBeVisible();
  await expect(page.locator("strong", { hasText: `${marker} (version 1)` })).toBeVisible();   // "Invoices print with: …"

  // 2. A draft invoice prints with it
  await page.goto('/sales/invoices/new');
  const customer = page.getByRole('combobox', { name: 'Customer' });
  await expect(customer.locator('option')).not.toHaveCount(1);
  await customer.selectOption({ index: 1 });
  const item = page.getByRole('combobox', { name: 'Item 1' });
  await expect(item.locator('option')).not.toHaveCount(1);
  await item.selectOption({ index: 1 });
  await page.getByRole('button', { name: 'Save draft' }).click();
  await expect(page.getByRole('heading', { name: /SI-\d{5}/ })).toBeVisible();
  const invoiceUrl = page.url();
  const number = (await page.getByRole('heading', { name: /SI-\d{5}/ }).textContent())!.match(/SI-\d{5}/)![0];

  await page.goto(`${invoiceUrl}/print`);
  await expect(page.getByText(`Layout: ${marker} (version 1)`)).toBeVisible();
  const layout = page.getByRole('document', { name: `${number} (${marker})` });
  await expect(layout.locator('.marker')).toHaveText(`${marker} ${number}`);
  await expect(layout.locator('script')).toHaveCount(0);
  expect(await page.title()).not.toBe('pwned');

  // 3. Back to the built-in layout; clean up
  await page.goto('/settings/print-templates');
  await page.getByRole('button', { name: 'Use built-in layout' }).click();
  await expect(page.locator('strong', { hasText: 'the built-in layout' })).toBeVisible();
  await page.goto(`${invoiceUrl}/print`);
  await expect(page.getByText('Built-in layout')).toBeVisible();
  await expect(page.getByRole('table', { name: 'Invoice lines' })).toBeVisible();

  await page.goto('/settings/print-templates');
  accept(page);
  await page.getByRole('row', { name: new RegExp(marker) }).getByRole('button', { name: 'Archive' }).click();
  await expect(page.getByRole('row', { name: new RegExp(marker) })).toHaveCount(0);
  await page.goto(invoiceUrl);
  accept(page);
  await page.getByRole('button', { name: 'Delete draft' }).click();
  await expect(page).toHaveURL(/\/sales\/invoices$/);
});

test('Admin role: Company tab but no Print templates tab', async ({ page }) => {
  await login(page, 'manager@demo.com', 'Manager@123', 'Admin');
  await page.goto('/settings/print-templates');
  await expect(page.getByRole('form', { name: 'Profile' })).toBeVisible();
  const tabs = page.getByRole('navigation', { name: /settings sections/i });
  await expect(tabs.getByRole('link', { name: 'Company' })).toBeVisible();
  await expect(tabs.getByRole('link', { name: 'Print templates' })).toHaveCount(0);
});

test('User role: no Print templates tab, but invoices still print', async ({ page }) => {
  await login(page, 'staff@demo.com', 'Staff@1234', 'User');
  await page.goto('/settings/print-templates');
  await expect(page.getByRole('form', { name: 'Profile' })).toBeVisible();
  await expect(page.getByRole('navigation', { name: /settings sections/i }).getByRole('link', { name: 'Print templates' })).toHaveCount(0);
});
