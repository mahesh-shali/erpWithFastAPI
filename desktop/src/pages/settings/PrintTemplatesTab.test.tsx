import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route as RouterRoute, Routes } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { DocumentPrint, InvoicePrint, PrintSetup, PrintTemplate } from '../../api/client';
import { calls, mockApi, renderScreen, type Route } from '../../test/utils';
import { DocumentPrintPage } from '../DocumentPrintPage';
import { InvoicePrintPage } from '../sales/InvoicePages';
import { SettingsPage } from './SettingsPage';

afterEach(() => vi.restoreAllMocks());

const body = (spy: ReturnType<typeof mockApi>, method: string, url: string) =>
  JSON.parse(String(spy.mock.calls.find(([u, i]) => String(u) === url && i?.method === method)![1]!.body));
const account = (role: string) => ({ id: 'u1', email: 'a@x.com', fullName: 'A', role, twoFactorEnabled: false, recoveryCodesLeft: 0 });
const tpl = (over: Partial<PrintTemplate> = {}): PrintTemplate => ({
  id: 't1', familyId: 'f1', pageKey: 'sales-invoices', name: 'Letterhead', version: 1, isDefault: true, isArchived: false,
  createdAt: '2026-11-26T10:00:00Z', body: '<h1>{{ invoice.number }}</h1>', ...over,
});
const page = (key: string, name: string, printable: boolean, formatCount = 0) => ({ key, name, printable, formatCount });
const setupFor = (over: Partial<PrintSetup> = {}): PrintSetup => ({
  isPlatformAdmin: false, currentOrganizationId: 'o1', organizations: [{ id: 'o1', name: 'Demo Corp' }],
  modules: [
    { key: 'master', name: 'Master', groups: [{ key: 'master-customers', name: 'Customers', pages: [page('customers', 'Customers', false)] }] },
    { key: 'sales', name: 'Sales & Labour Work', groups: [
      { key: 'sales-orders', name: 'Orders', pages: [page('customer-pos', 'Customer P.O.', true), page('orders', 'Sales Orders', false)] },
      { key: 'sales-invoices', name: 'Invoices', pages: [page('sales-invoices', 'Sales Invoices', true, 1)] },
    ] },
    { key: 'production', name: 'Production', groups: [
      { key: 'production-work-orders', name: 'Work Orders', pages: [page('work-orders', 'Work Orders', true)] },
    ] },
  ],
  ...over,
});
const empty = { items: [], page: 1, pageSize: 25, total: 0 };
const settings = (role: string, routes: Route[], setup: PrintSetup | ((b: unknown, url: string) => PrintSetup) = setupFor()) => renderScreen(
  <Routes><RouterRoute path="/settings/:tab?" element={<SettingsPage />} /></Routes>, [],
  [['GET', '/api/account', account(role)], ['GET', '/api/print-templates/setup*', setup],
    ['GET', '/api/sales-invoices*', empty], ['GET', '/api/customer-pos*', empty], ['GET', '/api/work-orders*', empty], ...routes],
  '/settings/print-templates');
/** Moves the pickers to Sales & Labour Work › Invoices › Sales Invoices. */
const toInvoices = async () => userEvent.selectOptions(await screen.findByRole('combobox', { name: 'Submodule' }), 'sales-invoices');
/** HTML inside a TemplateView's shadow root (Testing Library queries don't look into shadow DOM). */
const shadowHtml = (label: string) => screen.getByRole('document', { name: label }).shadowRoot!.innerHTML;

describe('Settings › Print templates', () => {
  const rendered = (text: string) => ({ templateId: null, name: 'Preview', version: 0, html: `<div class="print-root"><h1>${text}</h1></div>`, css: '' });

  it('picks Organization › Module › Submodule › Page, starting at the first page that can print', async () => {
    const { fetch } = settings('SuperAdmin', [
      ['GET', '/api/print-templates?pageKey=customer-pos', []],
      ['GET', '/api/print-templates?pageKey=sales-invoices', [tpl()]],
      ['GET', '/api/print-templates?pageKey=work-orders', []],
    ]);
    expect(await screen.findByLabelText('Selected page')).toHaveTextContent('Demo Corp › Sales & Labour Work › Orders › Customer P.O. › Print');
    expect(screen.getByRole('textbox', { name: 'Organization' })).toHaveValue('Demo Corp');       // not a platform admin: own only
    expect(screen.getByRole('option', { name: 'Sales Orders — no print yet' })).toBeDisabled();
    expect(await screen.findByText('No formats yet: customer P.O.s use the built-in layout.')).toBeInTheDocument();

    await toInvoices();
    expect(await screen.findByText('Letterhead (version 1)')).toBeInTheDocument();
    expect(screen.getByLabelText('Selected page')).toHaveTextContent('Demo Corp › Sales & Labour Work › Invoices › Sales Invoices › Print');

    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Module' }), 'production');
    expect(screen.getByLabelText('Selected page')).toHaveTextContent('Production › Work Orders › Work Orders › Print');
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Module' }), 'master');
    expect(await screen.findByText('Customers has no print screen yet, so it has no formats.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'New format' })).toBeNull();
    expect(calls(fetch)).toContain('GET /api/print-templates?pageKey=work-orders');
  });

  it('a new Customer P.O. format opens with a live preview of the starter, follows edits and saves as the default', async () => {
    let list: PrintTemplate[] = [];
    const { fetch } = settings('SuperAdmin', [
      ['GET', '/api/print-templates?pageKey=customer-pos', () => list],
      ['GET', '/api/print-templates/starter?pageKey=customer-pos', { pageKey: 'customer-pos', body: '<h1>{{ po.number }}</h1>' }],
      ['POST', '/api/print-templates/preview', (b: unknown) => rendered((b as { body: string }).body.includes('OA') ? 'OA CPO-00017' : 'CPO-00017')],
      ['POST', '/api/print-templates', (b: unknown) => { list = [tpl({ ...(b as object) })]; return list[0]; }],
    ]);
    expect(await screen.findByText('the built-in layout')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'New format' }));
    const form = await screen.findByRole('form', { name: 'Print template' });
    expect(within(form).getByRole('heading')).toHaveTextContent('New format for Customer P.O.');
    expect(within(form).getByLabelText('Template')).toHaveValue('<h1>{{ po.number }}</h1>');
    expect(within(form).getByText('po.number / customer_po_no / date / delivery_date / status')).toBeInTheDocument();

    // The preview appears by itself, next to the editor
    const pane = screen.getByRole('region', { name: 'Live preview' });
    await waitFor(() => expect(shadowHtml('Template preview')).toContain('CPO-00017'), { timeout: 3000 });
    expect(within(pane).getByRole('status')).toHaveTextContent('Up to date');
    expect(within(pane).getByRole('option', { name: 'Sample P.O.' })).toBeInTheDocument();
    expect(body(fetch, 'POST', '/api/print-templates/preview')).toEqual(
      { pageKey: 'customer-pos', body: '<h1>{{ po.number }}</h1>', documentId: null });

    // …and follows the edits once typing stops
    await userEvent.type(within(form).getByLabelText('Template'), 'OA');
    await waitFor(() => expect(shadowHtml('Template preview')).toContain('OA CPO-00017'), { timeout: 3000 });
    expect(calls(fetch).filter((c) => c === 'POST /api/print-templates/preview').length).toBeLessThan(4);   // debounced, not per key

    await userEvent.clear(within(form).getByLabelText('Name'));
    await userEvent.type(within(form).getByLabelText('Name'), 'Acknowledgement');
    await userEvent.click(within(form).getByRole('button', { name: 'Save as default' }));
    expect(await screen.findByText('Format saved.')).toBeInTheDocument();
    expect(body(fetch, 'POST', '/api/print-templates')).toMatchObject({ pageKey: 'customer-pos', name: 'Acknowledgement', makeDefault: true });
    expect(await screen.findByText('Acknowledgement (version 1)')).toBeInTheDocument();
  });

  it('a template error keeps the last good preview and says what is wrong', async () => {
    settings('SuperAdmin', [
      ['GET', '/api/print-templates?pageKey=customer-pos', []],
      ['GET', '/api/print-templates?pageKey=sales-invoices', [tpl()]],
      ['GET', '/api/print-templates/t1', tpl()],
      ['POST', '/api/print-templates/preview', (b: unknown) => ((b as { body: string }).body.includes('{{ for')
        ? new Response(JSON.stringify({ title: 'Template error: (1,30) : error : Missing end' }), { status: 400 })
        : rendered('GOOD'))],
    ]);
    await toInvoices();
    await userEvent.click(await screen.findByRole('button', { name: 'Edit & preview' }));
    const form = await screen.findByRole('form', { name: 'Print template' });
    expect(within(form).getByRole('heading')).toHaveTextContent('Edit Letterhead (version 1)');
    await waitFor(() => expect(shadowHtml('Template preview')).toContain('GOOD'), { timeout: 3000 });

    await userEvent.type(within(form).getByLabelText('Template'), '{{{{ for x in lines }}');
    expect(await screen.findByRole('alert', {}, { timeout: 3000 })).toHaveTextContent('Template error');
    expect(screen.getByRole('alert')).toHaveTextContent('The last working preview is shown below.');
    expect(shadowHtml('Template preview')).toContain('GOOD');
  });

  it('editing saves a new version; a save error from the API is shown', async () => {
    const { fetch } = settings('SuperAdmin', [
      ['GET', '/api/print-templates?pageKey=customer-pos', []],
      ['GET', '/api/print-templates?pageKey=sales-invoices', [tpl()]],
      ['GET', '/api/print-templates/t1', tpl()],
      ['POST', '/api/print-templates/preview', rendered('ok')],
      ['PUT', '/api/print-templates/t1', new Response(JSON.stringify({ title: 'Template error: (1,5) : error : Unexpected end' }), { status: 400 })],
    ]);
    await toInvoices();
    await userEvent.click(await screen.findByRole('button', { name: 'Edit & preview' }));
    const form = await screen.findByRole('form', { name: 'Print template' });
    await userEvent.click(within(form).getByRole('button', { name: 'Save new version' }));
    expect(await screen.findByText(/Unexpected end/)).toBeInTheDocument();
    expect(calls(fetch)).toContain('PUT /api/print-templates/t1');
  });

  it('switches back to the built-in layout', async () => {
    let list = [tpl()];
    const { fetch } = settings('SuperAdmin', [
      ['GET', '/api/print-templates?pageKey=customer-pos', []],
      ['GET', '/api/print-templates?pageKey=sales-invoices', () => list],
      ['POST', '/api/print-templates/use-built-in?pageKey=sales-invoices', () => { list = [tpl({ isDefault: false })]; return undefined; }],
    ]);
    await toInvoices();
    expect(await screen.findByText('Letterhead (version 1)')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Use built-in layout' }));
    expect(await screen.findByText('Sales Invoices now prints with the built-in layout.')).toBeInTheDocument();
    expect(screen.getByText('the built-in layout')).toBeInTheDocument();
    expect(calls(fetch)).toContain('POST /api/print-templates/use-built-in?pageKey=sales-invoices');
  });

  it('a platform admin picks another organization and works on its formats', async () => {
    const orgs = [{ id: 'o1', name: 'Demo Corp' }, { id: 'o2', name: 'Other Tenant Ltd' }];
    const { fetch } = settings('SuperAdmin', [
      ['GET', '/api/print-templates?pageKey=customer-pos', []],
      ['GET', '/api/print-templates?pageKey=customer-pos&organizationId=o2', [tpl({ id: 't2', pageKey: 'customer-pos', name: 'Their OA' })]],
      ['GET', '/api/print-templates/t2?organizationId=o2', tpl({ id: 't2', pageKey: 'customer-pos', name: 'Their OA' })],
      ['POST', '/api/print-templates/preview?organizationId=o2', rendered('Other Tenant Ltd')],
    ], (_b, url) => setupFor({ isPlatformAdmin: true, organizations: orgs, currentOrganizationId: url.includes('o2') ? 'o2' : 'o1' }));
    const org = await screen.findByRole('combobox', { name: 'Organization' });
    await userEvent.selectOptions(org, 'o2');
    expect(await screen.findByText('Their OA (version 1)')).toBeInTheDocument();
    expect(screen.getByLabelText('Selected page')).toHaveTextContent('Other Tenant Ltd › Sales & Labour Work › Orders › Customer P.O. › Print');
    expect(screen.getByText(/editing another organization’s formats as platform admin/)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Edit & preview' }));
    await waitFor(() => expect(shadowHtml('Template preview')).toContain('Other Tenant Ltd'), { timeout: 3000 });
    expect(screen.getByRole('combobox', { name: 'Preview with' })).toBeDisabled();                 // sample data only
    expect(body(fetch, 'POST', '/api/print-templates/preview?organizationId=o2')).toMatchObject({ documentId: null });
    expect(calls(fetch)).toContain('GET /api/print-templates/setup?organizationId=o2');
  });

  it.each(['Admin', 'Manager'])('is not offered to %s (Company still is to Admin)', async (role) => {
    settings(role, []);
    expect(await screen.findByRole('form', { name: 'Profile' })).toBeInTheDocument();
    const tabs = within(screen.getByRole('navigation', { name: /settings sections/i }));
    expect(tabs.queryByRole('link', { name: 'Print templates' })).toBeNull();
    if (role === 'Admin') expect(tabs.getByRole('link', { name: 'Company' })).toBeInTheDocument();
  });
});

describe('Invoice print with a custom layout', () => {
  const base = (over: Partial<InvoicePrint>): InvoicePrint => ({
    company: { name: 'Maker Ltd', address: null, state: null, phone: null, email: null, taxId: null },
    invoice: {
      id: 'i1', number: 'SI-00001', status: 'Submitted', invoiceDate: '2026-11-26', dueDate: '2026-12-26', customerId: 'c1', customerName: 'Acme',
      customerPoId: null, customerPoNumber: null, customerPoRef: null, storeId: 's1', store: 'Main', billingName: 'Acme', billingAddress: null,
      billingTaxId: null, billingState: null, shippingAddress: null, remarks: null, subTotal: 100, discountTotal: 0, taxTotal: 18, roundOff: 0,
      grandTotal: 118, costOfGoods: 0, interState: false, amountInWords: 'Rupees One Hundred Eighteen Only', taxSummary: [], createdAt: '',
      submittedAt: '', cancelledAt: null, lines: [],
    },
    ...over,
  });
  const custom = { templateId: 't1', name: 'Letterhead', version: 3, html: '<div class="print-root"><h1>CUSTOM SI-00001</h1></div>', css: '.print-root{color:red}' };
  const printAt = (data: InvoicePrint, routes: Route[] = []) => renderScreen(
    <Routes><RouterRoute path="/sales/invoices/:id/print" element={<InvoicePrintPage />} /></Routes>, [],
    [...routes, ['GET', '/api/sales-invoices/i1/print', data]], '/sales/invoices/i1/print');

  it('renders the template in a shadow root and names the layout', async () => {
    printAt(base({ template: custom }));
    expect(await screen.findByText('Layout: Letterhead (version 3)')).toBeInTheDocument();
    expect(shadowHtml('SI-00001 (Letterhead)')).toContain('CUSTOM SI-00001');
    expect(screen.queryByRole('table', { name: 'Invoice lines' })).not.toBeInTheDocument();   // not the built-in layout
    expect(screen.queryByRole('combobox', { name: 'Print format' })).toBeNull();               // only one format: no picker
  });

  it('the format picker switches to another format of the page', async () => {
    const formats = [
      { id: null, name: 'Built-in layout', version: 0, isDefault: false, isIssuedWith: false },
      { id: 't1', name: 'Letterhead', version: 3, isDefault: true, isIssuedWith: true },
      { id: 't2', name: 'Plain', version: 1, isDefault: false, isIssuedWith: false },
    ];
    const { fetch } = printAt(base({ template: custom, formats }), [
      ['GET', '/api/sales-invoices/i1/print?format=built-in', base({ template: null, formats })],
    ]);
    const picker = await screen.findByRole('combobox', { name: 'Print format' });
    expect(picker).toHaveValue('t1');
    expect(screen.getByRole('option', { name: 'Letterhead (version 3) · default · issued with' })).toBeInTheDocument();
    await userEvent.selectOptions(picker, 'built-in');
    expect(await screen.findByRole('table', { name: 'Invoice lines' })).toBeInTheDocument();
    expect(calls(fetch)).toContain('GET /api/sales-invoices/i1/print?format=built-in');
  });

  it('falls back to the built-in layout with a visible warning', async () => {
    printAt(base({ template: null,
      templateWarning: 'Template "Letterhead" (version 3) could not be used: loop limit. Printed with the built-in layout.' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('could not be used');
    expect(screen.getByText('Built-in layout')).toBeInTheDocument();
    expect(screen.getByRole('table', { name: 'Invoice lines' })).toBeInTheDocument();
  });
});

describe('Customer P.O. and work order print', () => {
  const doc = (over: Partial<DocumentPrint> = {}): DocumentPrint => ({
    pageKey: 'customer-pos', documentId: 'p1', number: 'CPO-00001', title: 'CPO-00001 · Acme',
    template: { templateId: null, name: 'Built-in layout', version: 0, html: '<div class="print-root"><h2>Order Acknowledgement</h2></div>', css: '' },
    formats: [
      { id: null, name: 'Built-in layout', version: 0, isDefault: true, isIssuedWith: false },
      { id: 't9', name: 'Their OA', version: 2, isDefault: false, isIssuedWith: false },
    ],
    ...over,
  });

  it('prints with the default, switches format and shows a fall-back warning', async () => {
    const { fetch } = renderScreen(
      <Routes><RouterRoute path="/sales/customer-po/:id/print" element={<DocumentPrintPage kind="customer-pos" />} /></Routes>, [],
      [['GET', '/api/customer-pos/p1/print?format=t9', doc({ templateWarning: 'Template "Their OA" (version 2) could not be used: x.' })],
        ['GET', '/api/customer-pos/p1/print', doc()]],
      '/sales/customer-po/p1/print');
    expect(await screen.findByText('Built-in layout')).toBeInTheDocument();
    expect(shadowHtml('CPO-00001 (Built-in layout)')).toContain('Order Acknowledgement');
    expect(screen.getByRole('link', { name: '‹ Back to CPO-00001' })).toHaveAttribute('href', '/sales/customer-po/p1');

    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Print format' }), 't9');
    expect(await screen.findByRole('alert')).toHaveTextContent('could not be used');
    expect(calls(fetch)).toContain('GET /api/customer-pos/p1/print?format=t9');
  });

  it('prints a work order with its own format', async () => {
    renderScreen(
      <Routes><RouterRoute path="/production/work-orders/:id/print" element={<DocumentPrintPage kind="work-orders" />} /></Routes>, [],
      [['GET', '/api/work-orders/w1/print', doc({ pageKey: 'work-orders', documentId: 'w1', number: 'WO-00003', formats: [],
        template: { templateId: 't5', name: 'Job sheet', version: 1, html: '<div class="print-root">JOB WO-00003</div>', css: '' } })]],
      '/production/work-orders/w1/print');
    expect(await screen.findByText('Layout: Job sheet (version 1)')).toBeInTheDocument();
    expect(shadowHtml('WO-00003 (Job sheet)')).toContain('JOB WO-00003');
    expect(screen.getByRole('link', { name: '‹ Back to WO-00003' })).toHaveAttribute('href', '/production/work-orders/w1');
  });
});
