import { expect, test, type Page } from '@playwright/test';

/**
 * Sales flow against the real stack (demo data): Item → Customer P.O. → Work Order → Sales Invoice → print.
 * Uses a new item and cancels everything it submits, so demo stock is unchanged (cancelled documents stay, as in any ERP).
 */
async function login(page: Page, email = 'admin@demo.com', password = 'Admin@123', role = 'SuperAdmin') {
  await page.goto('/login');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.locator('.user-chip .role')).toHaveText(role);
}

const accept = (page: Page) => page.once('dialog', (d) => d.accept());

test('item → P.O. → production → invoice → print, then everything cancelled', async ({ page }) => {
  const sku = `E2E-${Date.now().toString().slice(-6)}`;
  await login(page);

  // 1. Master › Inventory › Items: a finished good with GST 18%
  await page.goto('/master/items');
  await page.getByRole('button', { name: 'Add item' }).click();
  const itemForm = page.getByRole('form', { name: 'Add item' });
  await itemForm.getByLabel('SKU').fill(sku);
  await itemForm.getByLabel('Name').fill(`Valve ${sku}`);
  await itemForm.getByLabel('Price').fill('500');
  await itemForm.getByLabel('HSN / SAC code').fill('8481');
  await itemForm.getByRole('button', { name: 'Save' }).click();
  await expect(itemForm).toBeHidden();
  await page.getByRole('searchbox').fill(sku);                                   // the list shows the first 25 by name
  await expect(page.getByRole('row', { name: new RegExp(sku) })).toBeVisible();

  // 2. Customer P.O. for 3, confirmed
  await page.goto('/sales/customer-po/new');
  const customer = page.getByRole('combobox', { name: 'Customer' });
  await expect(customer.locator('option')).not.toHaveCount(1);
  await customer.selectOption({ index: 1 });
  await page.getByLabel('Customer P.O. no.').fill(`PO-${sku}`);
  await page.getByRole('combobox', { name: 'Item 1' }).selectOption({ label: `${sku} · Valve ${sku}` });
  await page.getByRole('spinbutton', { name: 'Quantity 1' }).fill('3');
  await expect(page.getByRole('table', { name: 'Items' })).toContainText('₹1,770.00');       // 1500 + 18%
  await page.getByRole('button', { name: 'Save draft' }).click();
  await page.getByRole('button', { name: 'Confirm P.O.' }).click();
  await expect(page.getByRole('status')).toContainText('Confirmed');
  const poUrl = page.url();

  // 3. Production for the line (no materials, ₹300 labour → ₹100 each)
  await page.getByRole('link', { name: 'Produce line 1' }).click();
  await expect(page.getByRole('spinbutton', { name: 'Quantity' })).toHaveValue('3');
  await page.getByLabel('Labour & overhead cost').fill('300');
  await page.getByRole('button', { name: 'Save draft' }).click();
  await page.getByRole('button', { name: 'Complete production' }).click();
  await expect(page.getByRole('status')).toContainText('Production completed');
  await expect(page.getByRole('region', { name: 'Production cost' })).toContainText('₹100.00');
  const woUrl = page.url();

  // 4. Stock details: 3 on hand, 3 committed
  await page.goto('/inventory/stock-details');
  await page.getByRole('searchbox').fill(sku);
  await expect(page.getByRole('row', { name: new RegExp(sku) })).toContainText('3');

  // 5. Invoice from the P.O., issued
  await page.goto(poUrl);
  await page.getByRole('link', { name: 'Create invoice' }).click();
  await expect(page.getByRole('spinbutton', { name: 'Quantity 1' })).toHaveValue('3');
  await page.getByRole('button', { name: 'Save draft' }).click();
  await page.getByRole('button', { name: 'Issue invoice' }).click();
  await expect(page.getByRole('status')).toContainText('Invoice issued');
  const invoiceUrl = page.url();
  await page.goto(poUrl);
  await expect(page.getByRole('heading', { level: 1 })).toContainText('Completed');

  // 6. Print layout
  await page.goto(`${invoiceUrl}/print`);
  const doc = page.getByRole('article', { name: 'Tax invoice' });
  await expect(doc).toContainText('Tax Invoice');
  await expect(doc).toContainText('Rupees One Thousand Seven Hundred Seventy Only');
  await expect(doc).toContainText(`PO-${sku}`);

  // 7. Clean up: cancel invoice, work order and P.O.; deactivate the item
  await page.goto(invoiceUrl);
  accept(page);
  await page.getByRole('button', { name: 'Cancel invoice' }).click();
  await expect(page.getByRole('status')).toContainText('Cancelled');
  await page.goto(woUrl);
  accept(page);
  await page.getByRole('button', { name: 'Cancel work order' }).click();
  await expect(page.getByRole('status')).toContainText('reversed');
  await page.goto(poUrl);
  accept(page);
  await page.getByRole('button', { name: 'Cancel P.O.' }).click();
  await expect(page.getByRole('status')).toContainText('Cancelled');
  await page.goto('/master/items');
  await page.getByRole('searchbox').fill(sku);
  await page.getByRole('row', { name: new RegExp(sku) }).getByRole('button', { name: 'Edit' }).click();
  await page.getByRole('form', { name: /Edit/ }).getByLabel('Active').uncheck();
  await page.getByRole('form', { name: /Edit/ }).getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('row', { name: new RegExp(sku) })).toContainText('Inactive');
});

test('User role: raises customer P.O.s but has no invoices or work orders', async ({ page }) => {
  await login(page, 'staff@demo.com', 'Staff@1234', 'User');
  await page.goto('/sales/customer-po');
  await expect(page.getByRole('link', { name: 'New P.O.' })).toBeVisible();
  await page.goto('/inventory/stock-details');
  await expect(page.getByRole('heading', { name: 'Stock details' })).toBeVisible();
  await page.goto('/sales/invoices');
  await expect(page.getByRole('heading', { name: 'No access' })).toBeVisible();
  await page.goto('/production/work-orders');
  await expect(page.getByRole('heading', { name: 'No access' })).toBeVisible();
});
