import { expect, test, type Page } from '@playwright/test';

/**
 * Inventory journey against the real stack (demo data). Works in its own new store and cancels what it
 * submits, so the demo stock is unchanged afterwards (cancelled entries stay visible, as in any ERP).
 */
async function login(page: Page, email = 'admin@demo.com', password = 'Admin@123', role = 'SuperAdmin') {
  await page.goto('/login');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.locator('.user-chip .role')).toHaveText(role);
}

test('receipt into a new store: draft → submit → stock position → ledger → cancel', async ({ page }) => {
  const code = `E2E${Date.now().toString().slice(-6)}`;
  await login(page);

  // 1. A new store
  await page.goto('/master/stores');
  await page.getByRole('button', { name: 'Add store' }).click();
  const storeForm = page.getByRole('form', { name: 'Add store' });
  await storeForm.getByLabel('Code').fill(code);
  await storeForm.getByLabel('Name').fill(`Store ${code}`);
  await storeForm.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('row', { name: new RegExp(`Store ${code}`) })).toBeVisible();

  // 2. Receipt draft: 7 × Product 1 at ₹250 into it
  await page.goto('/inventory/stock-addition/new');
  await page.getByRole('combobox', { name: 'To store' }).selectOption({ label: `Store ${code}` });
  const item = page.getByRole('combobox', { name: 'Item 1' });
  await expect(item.locator('option', { hasText: 'SKU-0001' })).toHaveCount(1);
  await item.selectOption({ label: 'SKU-0001 · Product 1' });
  await page.getByRole('spinbutton', { name: 'Quantity 1' }).fill('7');
  await page.getByRole('spinbutton', { name: 'Rate 1' }).fill('250');
  await page.getByRole('button', { name: 'Save draft' }).click();
  await expect(page.getByRole('heading', { name: /SE-\d{5}/ })).toBeVisible();
  await expect(page.getByText('Draft', { exact: true })).toBeVisible();

  // 3. Submit
  await page.getByRole('button', { name: 'Submit' }).click();
  await expect(page.getByRole('status')).toContainText('Stock has been updated');
  const number = (await page.getByRole('heading', { name: /SE-\d{5}/ }).textContent())!.match(/SE-\d{5}/)![0];

  // 4. Stock position shows it in the new store, valued 7 × 250
  await page.goto('/inventory/stock-position');
  await page.getByRole('combobox', { name: 'Store' }).selectOption({ label: `Store ${code}` });
  const row = page.getByRole('row', { name: /SKU-0001/ });
  await expect(row).toContainText('₹1,750.00');

  // 5. Ledger links back to the entry
  await row.getByRole('link', { name: 'Ledger' }).click();
  await expect(page.getByRole('heading', { name: 'Stock ledger' })).toBeVisible();
  await page.getByRole('link', { name: number }).first().click();

  // 6. Cancel reverses it
  page.once('dialog', (d) => d.accept());
  await page.getByRole('button', { name: 'Cancel entry' }).click();
  await expect(page.getByRole('status')).toContainText('reversed');
  await page.goto('/inventory/stock-position');
  await page.getByRole('combobox', { name: 'Store' }).selectOption({ label: `Store ${code}` });
  await expect(page.getByText('Nothing in stock for these filters.')).toBeVisible();
});

test('User role: can view stock position and ledger, not stock entries', async ({ page }) => {
  await login(page, 'staff@demo.com', 'Staff@1234', 'User');
  await page.goto('/inventory/stock-position');
  await expect(page.getByRole('table', { name: 'Stock position' }).locator('tbody tr').first()).toBeVisible();
  await page.goto('/reports/stock');
  await expect(page.getByRole('heading', { name: 'Stock ledger' })).toBeVisible();
  await page.goto('/inventory/stock-addition');
  await expect(page.getByRole('heading', { name: 'No access' })).toBeVisible();
});
