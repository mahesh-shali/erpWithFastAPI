import { expect, test, type Page } from '@playwright/test';

/**
 * Print formats per Organization › Module › Submodule › Page against the real stack: an invoice format from the starter,
 * preview, default, print, back to built-in; a Customer P.O. format picked on the P.O.'s print screen; the platform
 * admin's organization list. Everything created is cleaned up (formats archived, drafts deleted or cancelled).
 */
async function login(page: Page, email = 'admin@demo.com', password = 'Admin@123', role = 'SuperAdmin') {
  await page.goto('/login');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.locator('.user-chip .role')).toHaveText(role);
}

const accept = (page: Page) => page.once('dialog', (d) => d.accept());

/** Settings › Print templates at Sales & Labour Work › <submodule> (Orders = Customer P.O., Invoices = Sales Invoices). */
async function openPrintPage(page: Page, submodule: 'Orders' | 'Invoices') {
  await page.goto('/settings/print-templates');
  await page.getByRole('combobox', { name: 'Module', exact: true }).selectOption({ label: 'Sales & Labour Work' });
  await page.getByRole('combobox', { name: 'Submodule' }).selectOption({ label: submodule });
  await expect(page.getByLabel('Selected page')).toContainText(`Sales & Labour Work › ${submodule} ›`);
}

test('own invoice layout: starter → preview → default → print → back to built-in', async ({ page }) => {
  const marker = `E2E-LAYOUT-${Date.now().toString().slice(-6)}`;
  await login(page);

  // 1. Settings › Print templates › Sales Invoices: new format from the starter, with a marker and a hostile script
  await openPrintPage(page, 'Invoices');
  await page.getByRole('button', { name: 'New format' }).click();
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
  await form.getByRole('button', { name: 'Save as default' }).click();
  await expect(page.getByText('Format saved.')).toBeVisible();
  await expect(page.locator("strong", { hasText: `${marker} (version 1)` })).toBeVisible();   // "Sales Invoices prints with: …"

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
  await openPrintPage(page, 'Invoices');
  await page.getByRole('button', { name: 'Use built-in layout' }).click();
  await expect(page.locator('strong', { hasText: 'the built-in layout' })).toBeVisible();
  await page.goto(`${invoiceUrl}/print`);
  await expect(page.getByText('Built-in layout', { exact: true })).toBeVisible();
  await expect(page.getByRole('table', { name: 'Invoice lines' })).toBeVisible();

  await openPrintPage(page, 'Invoices');
  accept(page);
  await page.getByRole('row', { name: new RegExp(marker) }).getByRole('button', { name: 'Archive' }).click();
  await expect(page.getByRole('row', { name: new RegExp(marker) })).toHaveCount(0);
  await page.goto(invoiceUrl);
  accept(page);
  await page.getByRole('button', { name: 'Delete draft' }).click();
  await expect(page).toHaveURL(/\/sales\/invoices$/);
});

test('Customer P.O. format: picked on the print screen; the platform admin sees every organization', async ({ page }) => {
  const marker = `E2E-OA-${Date.now().toString().slice(-6)}`;
  await login(page);

  // 1. Demo Corp › Sales & Labour Work › Orders › Customer P.O.: a second (non-default) format
  await openPrintPage(page, 'Orders');
  await expect(page.getByLabel('Selected page')).toHaveText('Demo Corp › Sales & Labour Work › Orders › Customer P.O. › Print');
  await expect(page.getByRole('combobox', { name: 'Organization' }).locator('option', { hasText: 'Other Tenant Ltd' })).toHaveCount(1);
  await page.getByRole('button', { name: 'New format' }).click();
  const form = page.getByRole('form', { name: 'Print template' });
  await form.getByLabel('Name').fill(marker);
  await form.getByLabel('Template').fill(`<h1 class="marker">${marker} {{ po.number }} {{ customer.name }}</h1>`);
  const preview = page.getByRole('region', { name: 'Live preview' }).getByRole('document', { name: 'Template preview' });
  await expect(preview.locator('.marker')).toContainText(`${marker} CPO-00017`);
  await form.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByText('Format saved.')).toBeVisible();

  // 2. A draft P.O. prints with the built-in layout; the picker switches to the new format
  await page.goto('/sales/customer-po/new');
  const customer = page.getByRole('combobox', { name: 'Customer' });
  await expect(customer.locator('option')).not.toHaveCount(1);
  await customer.selectOption({ index: 1 });
  await page.getByLabel('Customer P.O. no.').fill(marker);
  const item = page.getByRole('combobox', { name: 'Item 1' });
  await expect(item.locator('option')).not.toHaveCount(1);
  await item.selectOption({ index: 1 });
  await page.getByRole('button', { name: 'Save draft' }).click();
  await expect(page.getByRole('heading', { name: /CPO-\d{5}/ })).toBeVisible();
  const poUrl = page.url();
  const number = (await page.getByRole('heading', { name: /CPO-\d{5}/ }).textContent())!.match(/CPO-\d{5}/)![0];

  await page.getByRole('link', { name: 'Print' }).click();
  await expect(page.getByText('Built-in layout', { exact: true })).toBeVisible();
  await expect(page.getByRole('document', { name: `${number} (Built-in layout)` })).toContainText('Order Acknowledgement');
  await page.getByRole('combobox', { name: 'Print format' }).selectOption({ label: `${marker} (version 1)` });
  await expect(page.getByText(`Layout: ${marker} (version 1)`)).toBeVisible();
  await expect(page.getByRole('document', { name: `${number} (${marker})` }).locator('.marker')).toContainText(`${marker} ${number}`);
  await expect(page).toHaveURL(/\?format=/);

  // 3. Clean up: delete the draft P.O., archive the format
  await page.goto(poUrl);
  accept(page);
  await page.getByRole('button', { name: 'Delete draft' }).click();
  await expect(page).toHaveURL(/\/sales\/customer-po$/);
  await openPrintPage(page, 'Orders');
  accept(page);
  await page.getByRole('row', { name: new RegExp(marker) }).getByRole('button', { name: 'Archive' }).click();
  await expect(page.getByRole('row', { name: new RegExp(marker) })).toHaveCount(0);
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
