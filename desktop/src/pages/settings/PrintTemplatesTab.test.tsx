import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route as RouterRoute, Routes } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { InvoicePrint, PrintTemplate } from '../../api/client';
import { calls, mockApi, renderScreen, type Route } from '../../test/utils';
import { InvoicePrintPage } from '../sales/InvoicePages';
import { SettingsPage } from './SettingsPage';

afterEach(() => vi.restoreAllMocks());

const body = (spy: ReturnType<typeof mockApi>, method: string, url: string) =>
  JSON.parse(String(spy.mock.calls.find(([u, i]) => String(u) === url && i?.method === method)![1]!.body));
const account = (role: string) => ({ id: 'u1', email: 'a@x.com', fullName: 'A', role, twoFactorEnabled: false, recoveryCodesLeft: 0 });
const tpl = (over: Partial<PrintTemplate> = {}): PrintTemplate => ({
  id: 't1', familyId: 'f1', documentType: 'sales-invoice', name: 'Letterhead', version: 1, isDefault: true, isArchived: false,
  createdAt: '2026-11-26T10:00:00Z', body: '<h1>{{ invoice.number }}</h1>', ...over,
});
const settings = (role: string, routes: Route[]) => renderScreen(
  <Routes><RouterRoute path="/settings/:tab?" element={<SettingsPage />} /></Routes>, [],
  [['GET', '/api/account', account(role)], ['GET', '/api/sales-invoices*', { items: [], page: 1, pageSize: 25, total: 0 }], ...routes],
  '/settings/print-templates');
/** HTML inside a TemplateView's shadow root (Testing Library queries don't look into shadow DOM). */
const shadowHtml = (label: string) => screen.getByRole('document', { name: label }).shadowRoot!.innerHTML;

describe('Settings › Print templates', () => {
  const rendered = (text: string) => ({ templateId: null, name: 'Preview', version: 0, html: `<div class="print-root"><h1>${text}</h1></div>`, css: '' });

  it('opens with a live preview of the starter, follows edits and saves as the default', async () => {
    let list: PrintTemplate[] = [];
    const { fetch } = settings('SuperAdmin', [
      ['GET', '/api/print-templates?documentType=sales-invoice', () => list],
      ['GET', '/api/print-templates/starter?documentType=sales-invoice', { documentType: 'sales-invoice', body: '<h1>{{ invoice.number }}</h1>' }],
      ['POST', '/api/print-templates/preview', (b: unknown) => rendered((b as { body: string }).body.includes('Bill') ? 'Bill SI-00042' : 'SI-00042')],
      ['POST', '/api/print-templates', (b: unknown) => { list = [tpl({ ...(b as object) })]; return list[0]; }],
    ]);
    expect(await screen.findByText('the built-in layout')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'New template' }));
    const form = await screen.findByRole('form', { name: 'Print template' });
    expect(within(form).getByLabelText('Template')).toHaveValue('<h1>{{ invoice.number }}</h1>');

    // The preview appears by itself, next to the editor
    const pane = screen.getByRole('region', { name: 'Live preview' });
    await waitFor(() => expect(shadowHtml('Template preview')).toContain('SI-00042'), { timeout: 3000 });
    expect(within(pane).getByRole('status')).toHaveTextContent('Up to date');
    expect(body(fetch, 'POST', '/api/print-templates/preview')).toEqual(
      { documentType: 'sales-invoice', body: '<h1>{{ invoice.number }}</h1>', invoiceId: null });

    // …and follows the edits once typing stops
    await userEvent.type(within(form).getByLabelText('Template'), 'Bill');
    await waitFor(() => expect(shadowHtml('Template preview')).toContain('Bill SI-00042'), { timeout: 3000 });
    expect(calls(fetch).filter((c) => c === 'POST /api/print-templates/preview').length).toBeLessThan(4);   // debounced, not per key

    await userEvent.clear(within(form).getByLabelText('Name'));
    await userEvent.type(within(form).getByLabelText('Name'), 'Letterhead');
    await userEvent.click(within(form).getByRole('button', { name: 'Save and use for invoices' }));
    expect(await screen.findByText('Template saved.')).toBeInTheDocument();
    expect(body(fetch, 'POST', '/api/print-templates')).toMatchObject({ documentType: 'sales-invoice', name: 'Letterhead', makeDefault: true });
    expect(await screen.findByText('Letterhead (version 1)')).toBeInTheDocument();
  });

  it('a template error keeps the last good preview and says what is wrong', async () => {
    settings('SuperAdmin', [
      ['GET', '/api/print-templates?documentType=sales-invoice', [tpl()]],
      ['GET', '/api/print-templates/t1', tpl()],
      ['POST', '/api/print-templates/preview', (b: unknown) => ((b as { body: string }).body.includes('{{ for')
        ? new Response(JSON.stringify({ title: 'Template error: (1,30) : error : Missing end' }), { status: 400 })
        : rendered('GOOD'))],
    ]);
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
      ['GET', '/api/print-templates?documentType=sales-invoice', [tpl()]],
      ['GET', '/api/print-templates/t1', tpl()],
      ['POST', '/api/print-templates/preview', rendered('ok')],
      ['PUT', '/api/print-templates/t1', new Response(JSON.stringify({ title: 'Template error: (1,5) : error : Unexpected end' }), { status: 400 })],
    ]);
    await userEvent.click(await screen.findByRole('button', { name: 'Edit & preview' }));
    const form = await screen.findByRole('form', { name: 'Print template' });
    await userEvent.click(within(form).getByRole('button', { name: 'Save new version' }));
    expect(await screen.findByText(/Unexpected end/)).toBeInTheDocument();
    expect(calls(fetch)).toContain('PUT /api/print-templates/t1');
  });

  it('switches back to the built-in layout', async () => {
    let list = [tpl()];
    const { fetch } = settings('SuperAdmin', [
      ['GET', '/api/print-templates?documentType=sales-invoice', () => list],
      ['POST', '/api/print-templates/use-built-in?documentType=sales-invoice', () => { list = [tpl({ isDefault: false })]; return undefined; }],
    ]);
    expect(await screen.findByText('Letterhead (version 1)')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Use built-in layout' }));
    expect(await screen.findByText('the built-in layout')).toBeInTheDocument();
    expect(calls(fetch)).toContain('POST /api/print-templates/use-built-in?documentType=sales-invoice');
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
  const printAt = (data: InvoicePrint) => renderScreen(
    <Routes><RouterRoute path="/sales/invoices/:id/print" element={<InvoicePrintPage />} /></Routes>, [],
    [['GET', '/api/sales-invoices/i1/print', data]], '/sales/invoices/i1/print');

  it('renders the template in a shadow root and names the layout', async () => {
    printAt(base({ template: { templateId: 't1', name: 'Letterhead', version: 3,
      html: '<div class="print-root"><h1>CUSTOM SI-00001</h1></div>', css: '.print-root{color:red}' } }));
    expect(await screen.findByText('Layout: Letterhead (version 3)')).toBeInTheDocument();
    expect(shadowHtml('SI-00001 (Letterhead)')).toContain('CUSTOM SI-00001');
    expect(screen.queryByRole('table', { name: 'Invoice lines' })).not.toBeInTheDocument();   // not the built-in layout
  });

  it('falls back to the built-in layout with a visible warning', async () => {
    printAt(base({ template: null,
      templateWarning: 'Template "Letterhead" (version 3) could not be used: loop limit. Printed with the built-in layout.' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('could not be used');
    expect(screen.getByText('Built-in layout')).toBeInTheDocument();
    expect(screen.getByRole('table', { name: 'Invoice lines' })).toBeInTheDocument();
  });
});
