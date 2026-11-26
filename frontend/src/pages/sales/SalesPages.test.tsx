import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route as RouterRoute, Routes } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type {
  Customer, CustomerPo, InvoicePrint, SalesInvoice, StockDetailsRow, StockItemDetails, Store, WorkOrder,
} from '../../api/client';
import { calls, mockApi, perm, renderScreen, type Route } from '../../test/utils';
import { CustomersPage } from '../DataPages';
import { StockDetailsPage, StockItemDetailsPage } from '../inventory/StockDetailsPages';
import { WorkOrderPage, WorkOrdersPage } from '../production/WorkOrderPages';
import { SettingsPage } from '../settings/SettingsPage';
import { CustomerPoPage, CustomerPosPage } from './CustomerPoPages';
import { InvoicePrintPage, SalesInvoicePage, lineMaths } from './InvoicePages';

afterEach(() => vi.restoreAllMocks());

const body = (spy: ReturnType<typeof mockApi>, method: string, url: string) =>
  JSON.parse(String(spy.mock.calls.find(([u, i]) => String(u) === url && i?.method === method)![1]!.body));
const paged = <T,>(items: T[]) => ({ items, page: 1, pageSize: 25, total: items.length });

const customers: Customer[] = [
  { id: 'c1', name: 'Acme Traders', email: null, city: 'Pune', country: 'India', state: 'Maharashtra', paymentTermsDays: 30 },
];
const stores: Store[] = [{ id: 's1', code: 'MAIN', name: 'Main Store', location: null, isDefault: true, isActive: true }];
const items = [
  { id: 'fg', sku: 'FG-1', name: 'Valve', uom: 'NOS', stockQuantity: 2, unitPrice: 500, itemType: 'Finished Good', taxRate: 18, hsnCode: '8481' },
  { id: 'rm', sku: 'RM-1', name: 'Brass rod', uom: 'KG', stockQuantity: 90, unitPrice: 60, itemType: 'Raw Material', taxRate: 18, hsnCode: null },
];
const LOOKUPS: Route[] = [
  ['GET', '/api/sales/lookups/customers', customers],
  ['GET', '/api/stock/items', items],
  ['GET', '/api/stock/stores', stores],
];

const po = (over: Partial<CustomerPo> = {}): CustomerPo => ({
  id: 'po1', number: 'CPO-00001', customerId: 'c1', customerName: 'Acme Traders', customerPoNo: 'ACME/77', poDate: '2026-11-20',
  deliveryDate: '2026-12-01', status: 'Confirmed', remarks: null, subTotal: 5000, taxTotal: 900, grandTotal: 5900,
  createdAt: '', confirmedAt: '', completedAt: null, cancelledAt: null,
  lines: [{ id: 'l1', lineNo: 1, productId: 'fg', sku: 'FG-1', productName: 'Valve', uom: 'NOS', hsnCode: '8481', quantity: 10, rate: 500,
    taxRate: 18, amount: 5000, taxAmount: 900, producedQty: 6, invoicedQty: 2, toProduce: 4, toInvoice: 8, inStock: 4 }],
  workOrders: [{ id: 'w1', number: 'WO-00001', status: 'Submitted', date: '2026-11-21', amount: 900, detail: 'FG-1 × 6' }],
  invoices: [], ...over,
});

function at(path: string, element: JSX.Element, route: string, pages: ReturnType<typeof perm>[], routes: Route[]) {
  return renderScreen(<Routes><RouterRoute path={path} element={element} /></Routes>, pages, [...LOOKUPS, ...routes], route);
}

// ---------------------------------------------------------------- Customers
describe('Customers screen: add with billing details', () => {
  it('posts every field of the form', async () => {
    const { fetch } = renderScreen(<CustomersPage />, [perm('customers', 'VRWU')], [
      ['GET', '/api/customers*', paged([])],
      ['POST', '/api/customers', (b: unknown) => ({ id: 'c9', ...(b as object) })],
    ]);
    await userEvent.click(await screen.findByRole('button', { name: 'Add customer' }));
    const form = screen.getByRole('form', { name: 'Add customer' });
    await userEvent.type(within(form).getByLabelText('Name'), 'Beta Pumps');
    await userEvent.type(within(form).getByLabelText('GSTIN'), '29ABCDE1234F1Z5');
    await userEvent.type(within(form).getByLabelText('State'), 'Karnataka');
    await userEvent.type(within(form).getByLabelText('Address'), 'Peenya');
    await userEvent.clear(within(form).getByLabelText('Payment terms (days)'));
    await userEvent.type(within(form).getByLabelText('Payment terms (days)'), '45');
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(calls(fetch)).toContain('POST /api/customers'));
    expect(body(fetch, 'POST', '/api/customers')).toEqual({
      name: 'Beta Pumps', contactPerson: null, email: null, phone: null, taxId: '29ABCDE1234F1Z5', address: 'Peenya', city: null,
      state: 'Karnataka', postalCode: null, country: 'India', paymentTermsDays: 45,
    });
  });
});

// ---------------------------------------------------------------- Customer P.O.
describe('Customer P.O. screens', () => {
  it('lists P.O.s with progress and filters by status', async () => {
    const { fetch } = renderScreen(<CustomerPosPage />, [perm('customer-pos', 'VRW')], [['GET', '/api/customer-pos*', paged([{
      id: 'po1', number: 'CPO-00001', customerName: 'Acme Traders', customerPoNo: 'ACME/77', poDate: '2026-11-20', deliveryDate: null,
      status: 'Confirmed', lineCount: 1, grandTotal: 5900, orderedQty: 10, producedQty: 6, invoicedQty: 2, createdAt: '' }])]]);
    const row = (await screen.findByRole('link', { name: 'CPO-00001' })).closest('tr')!;
    expect(row).toHaveTextContent('ACME/77');
    expect(row).toHaveTextContent('₹5,900.00');
    expect(row).toHaveTextContent('Confirmed');
    expect(screen.getByRole('link', { name: 'New P.O.' })).toHaveAttribute('href', '/sales/customer-po/new');
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Status' }), 'Draft');
    await waitFor(() => expect(calls(fetch)).toContain('GET /api/customer-pos?status=Draft&page=1&pageSize=25'));
  });

  it('creates a draft with the item price and GST filled in, and totals with tax', async () => {
    const draft = po({ status: 'Draft', workOrders: [] });
    const { fetch } = at('/sales/customer-po/:id', <CustomerPoPage />, '/sales/customer-po/new', [perm('customer-pos', 'VRWUD')], [
      ['POST', '/api/customer-pos', draft], ['GET', '/api/customer-pos/po1', draft],
    ]);
    await screen.findByRole('option', { name: 'FG-1 · Valve' });
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Customer' }), 'c1');
    await userEvent.type(screen.getByLabelText('Customer P.O. no.'), 'ACME/77');
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Item 1' }), 'fg');
    expect(screen.getByRole('spinbutton', { name: 'Rate 1' })).toHaveValue(500);
    expect(screen.getByRole('spinbutton', { name: 'GST 1' })).toHaveValue(18);
    await userEvent.clear(screen.getByRole('spinbutton', { name: 'Quantity 1' }));
    await userEvent.type(screen.getByRole('spinbutton', { name: 'Quantity 1' }), '10');
    expect(screen.getByRole('table', { name: 'Items' })).toHaveTextContent('₹5,900.00');      // 5000 + 18%

    await userEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    expect(await screen.findByRole('button', { name: 'Confirm P.O.' })).toBeInTheDocument();
    expect(body(fetch, 'POST', '/api/customer-pos')).toMatchObject({
      customerId: 'c1', customerPoNo: 'ACME/77', lines: [{ productId: 'fg', quantity: 10, rate: 500, taxRate: 18 }],
    });
  });

  it('a confirmed P.O. links to production and invoicing and shows its documents', async () => {
    at('/sales/customer-po/:id', <CustomerPoPage />, '/sales/customer-po/po1',
      [perm('customer-pos', 'VRWUD'), perm('work-orders', 'VRW'), perm('sales-invoices', 'VRW')],
      [['GET', '/api/customer-pos/po1', po()]]);
    expect(await screen.findByRole('link', { name: 'Produce line 1' })).toHaveAttribute('href', '/production/work-orders/new?poId=po1&lineId=l1');
    expect(screen.getByRole('link', { name: 'Create invoice' })).toHaveAttribute('href', '/sales/invoices/new?poId=po1');
    expect(screen.getByRole('button', { name: 'Cancel P.O.' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save draft' })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'WO-00001' })).toHaveAttribute('href', '/production/work-orders/w1');
  });

  it('without production and invoice rights there are no links to raise them', async () => {
    at('/sales/customer-po/:id', <CustomerPoPage />, '/sales/customer-po/po1', [perm('customer-pos', 'VR')],
      [['GET', '/api/customer-pos/po1', po()]]);
    await screen.findByRole('heading', { name: /CPO-00001/ });
    expect(screen.queryByRole('link', { name: 'Create invoice' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Produce line 1' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Cancel P.O.' })).not.toBeInTheDocument();
  });
});

// ---------------------------------------------------------------- Work orders
const wo = (over: Partial<WorkOrder> = {}): WorkOrder => ({
  id: 'w1', number: 'WO-00001', status: 'Draft', postingDate: '2026-11-21', customerPoId: 'po1', customerPoNumber: 'CPO-00001',
  customerName: 'Acme Traders', customerPoLineId: 'l1', productId: 'fg', sku: 'FG-1', productName: 'Valve', uom: 'NOS', quantity: 4,
  storeId: 's1', store: 'Main Store', materialStoreId: 's1', materialStore: 'Main Store', otherCost: 200, materialCost: 0, unitCost: 0,
  totalCost: 0, remarks: null, createdAt: '', submittedAt: null, cancelledAt: null,
  materials: [{ productId: 'rm', sku: 'RM-1', productName: 'Brass rod', uom: 'KG', quantity: 8, rate: 0, amount: 0, inStock: 90 }], ...over,
});

describe('Work order screens', () => {
  it('lists work orders, showing a completed one as Completed', async () => {
    renderScreen(<WorkOrdersPage />, [perm('work-orders', 'VR')], [['GET', '/api/work-orders*', paged([{
      id: 'w1', number: 'WO-00001', status: 'Submitted', postingDate: '2026-11-21', customerPoNumber: 'CPO-00001', customerName: 'Acme Traders',
      sku: 'FG-1', productName: 'Valve', quantity: 6, store: 'Main Store', totalCost: 900, createdAt: '' }])]]);
    const row = (await screen.findByRole('link', { name: 'WO-00001' })).closest('tr')!;
    expect(row).toHaveTextContent('Completed');
    expect(row).toHaveTextContent('₹900.00');
    expect(screen.queryByRole('link', { name: 'New work order' })).not.toBeInTheDocument();
  });

  it('from a P.O. line: fills the item and the quantity still to produce, and saves materials', async () => {
    const { fetch } = at('/production/work-orders/:id', <WorkOrderPage />, '/production/work-orders/new?poId=po1&lineId=l1',
      [perm('work-orders', 'VRWUD')], [
        ['GET', '/api/sales/lookups/open-pos', [{ id: 'po1', number: 'CPO-00001', status: 'Confirmed', date: '2026-11-20', amount: 5900, detail: 'Acme Traders · ACME/77' }]],
        ['GET', '/api/customer-pos/po1', po()],
        ['POST', '/api/work-orders', wo()], ['GET', '/api/work-orders/w1', wo()],
      ]);
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'P.O. line' })).toHaveValue('l1'));
    expect(screen.getByRole('spinbutton', { name: 'Quantity' })).toHaveValue(4);
    expect(screen.getByText(/4 still to produce/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: '+ Add material' }));
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Material 1' }), 'rm');
    await userEvent.clear(screen.getByRole('spinbutton', { name: 'Material quantity 1' }));
    await userEvent.type(screen.getByRole('spinbutton', { name: 'Material quantity 1' }), '8');
    await userEvent.clear(screen.getByLabelText('Labour & overhead cost'));
    await userEvent.type(screen.getByLabelText('Labour & overhead cost'), '200');
    await userEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    expect(await screen.findByRole('button', { name: 'Complete production' })).toBeInTheDocument();
    expect(body(fetch, 'POST', '/api/work-orders')).toMatchObject({
      customerPoLineId: 'l1', productId: 'fg', quantity: 4, storeId: 's1', materialStoreId: 's1', otherCost: 200,
      materials: [{ productId: 'rm', quantity: 8 }],
    });
  });

  it('completing production shows the cost per unit', async () => {
    const done = wo({ status: 'Submitted', materialCost: 400, totalCost: 600, unitCost: 150, submittedAt: '2026-11-21T10:00:00Z',
      materials: [{ productId: 'rm', sku: 'RM-1', productName: 'Brass rod', uom: 'KG', quantity: 8, rate: 50, amount: 400, inStock: 82 }] });
    const { fetch } = at('/production/work-orders/:id', <WorkOrderPage />, '/production/work-orders/w1', [perm('work-orders', 'VRWUD')], [
      ['GET', '/api/work-orders/w1', wo()], ['GET', '/api/customer-pos/po1', po()], ['POST', '/api/work-orders/w1/submit', done],
    ]);
    await userEvent.click(await screen.findByRole('button', { name: 'Complete production' }));
    expect(await screen.findByRole('status')).toHaveTextContent('Production completed');
    expect(calls(fetch)).toContain('POST /api/work-orders/w1/submit');
    const cost = screen.getByRole('region', { name: 'Production cost' });
    expect(cost).toHaveTextContent('₹600.00');
    expect(cost).toHaveTextContent('₹150.00');
    expect(screen.getByRole('button', { name: 'Cancel work order' })).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------- Sales invoices
const invoice = (over: Partial<SalesInvoice> = {}): SalesInvoice => ({
  id: 'i1', number: 'SI-00001', status: 'Draft', invoiceDate: '2026-11-22', dueDate: '2026-12-22', customerId: 'c1', customerName: 'Acme Traders',
  customerPoId: 'po1', customerPoNumber: 'CPO-00001', customerPoRef: 'ACME/77', storeId: 's1', store: 'Main Store',
  billingName: 'Acme Traders', billingAddress: 'Plot 7\nPune 411019\nMaharashtra', billingTaxId: '27ABCDE1234F1Z5', billingState: 'Maharashtra',
  shippingAddress: null, remarks: null, subTotal: 4000, discountTotal: 0, taxTotal: 720, roundOff: 0, grandTotal: 4720, costOfGoods: 0,
  interState: false, amountInWords: 'Rupees Four Thousand Seven Hundred Twenty Only', taxSummary: [{ taxRate: 18, taxable: 4000, tax: 720 }],
  createdAt: '', submittedAt: null, cancelledAt: null,
  lines: [{ lineNo: 1, productId: 'fg', sku: 'FG-1', productName: 'Valve', uom: 'NOS', hsnCode: '8481', customerPoLineId: 'l1', quantity: 8, rate: 500,
    discountPercent: 0, amount: 4000, taxRate: 18, taxAmount: 720, total: 4720, costRate: 0, inStock: 4 }], ...over,
});

describe('Sales invoice screens', () => {
  it('line maths match the API: discount first, then GST, each rounded to paise', () => {
    expect(lineMaths(3, 99.99, 10, 18)).toEqual({ gross: 299.97, discount: 30, amount: 269.97, tax: 48.59, total: 318.56 });
  });

  it('from a P.O.: takes its customer and the lines still to invoice', async () => {
    const { fetch } = at('/sales/invoices/:id', <SalesInvoicePage />, '/sales/invoices/new?poId=po1', [perm('sales-invoices', 'VRWUD')], [
      ['GET', '/api/sales/lookups/open-pos*', [{ id: 'po1', number: 'CPO-00001', status: 'Confirmed', date: '2026-11-20', amount: 5900, detail: 'Acme Traders · ACME/77' }]],
      ['GET', '/api/customer-pos/po1', po()],
      ['POST', '/api/sales-invoices', invoice()], ['GET', '/api/sales-invoices/i1', invoice()],
    ]);
    await waitFor(() => expect(screen.getByRole('spinbutton', { name: 'Quantity 1' })).toHaveValue(8));
    expect(screen.getByRole('combobox', { name: 'Customer' })).toHaveValue('c1');
    expect(screen.getByText(/8 left to invoice/)).toBeInTheDocument();
    expect(screen.getByRole('table', { name: 'Items' })).toHaveTextContent('₹4,720.00');
    await userEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    expect(await screen.findByRole('button', { name: 'Issue invoice' })).toBeInTheDocument();
    expect(body(fetch, 'POST', '/api/sales-invoices')).toMatchObject({
      customerId: 'c1', customerPoId: 'po1', storeId: 's1',
      lines: [{ customerPoLineId: 'l1', productId: 'fg', quantity: 8, rate: 500, discountPercent: 0, taxRate: 18 }],
    });
    expect(screen.getByRole('link', { name: 'Print' })).toHaveAttribute('href', '/sales/invoices/i1/print');
  });

  it('issuing shows the issued invoice, read-only, with Cancel', async () => {
    const issued = invoice({ status: 'Submitted', costOfGoods: 1200, submittedAt: '2026-11-22T10:00:00Z' });
    const { fetch } = at('/sales/invoices/:id', <SalesInvoicePage />, '/sales/invoices/i1', [perm('sales-invoices', 'VRWUD')], [
      ['GET', '/api/sales-invoices/i1', invoice()], ['GET', '/api/customer-pos/po1', po()], ['POST', '/api/sales-invoices/i1/submit', issued],
    ]);
    await userEvent.click(await screen.findByRole('button', { name: 'Issue invoice' }));
    expect(await screen.findByRole('status')).toHaveTextContent('Invoice issued');
    expect(calls(fetch)).toContain('POST /api/sales-invoices/i1/submit');
    expect(screen.getByRole('heading', { name: /SI-00001/ })).toHaveTextContent('Issued');
    expect(screen.queryByRole('spinbutton', { name: 'Quantity 1' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Cancel invoice' })).toBeInTheDocument();
    expect(screen.getByText(/Cost of goods ₹1,200.00/)).toBeInTheDocument();
  });

  it('prints a tax invoice with CGST/SGST split, amount in words and a draft watermark', async () => {
    const print: InvoicePrint = {
      company: { name: 'Maker Ltd', address: 'Chakan', state: 'Maharashtra', phone: null, email: null, taxId: '27AAACM0000A1Z1' },
      invoice: invoice(),
    };
    const printSpy = vi.spyOn(window, 'print').mockImplementation(() => undefined);
    at('/sales/invoices/:id/print', <InvoicePrintPage />, '/sales/invoices/i1/print', [perm('sales-invoices', 'VR')],
      [['GET', '/api/sales-invoices/i1/print', print]]);
    const doc = await screen.findByRole('article', { name: 'Tax invoice' });
    expect(doc).toHaveTextContent('Maker Ltd');
    expect(doc).toHaveTextContent('GSTIN: 27AAACM0000A1Z1');
    expect(doc).toHaveTextContent('ACME/77');
    expect(doc).toHaveTextContent('Rupees Four Thousand Seven Hundred Twenty Only');
    expect(doc).toHaveTextContent('DRAFT');
    const tax = screen.getByRole('table', { name: 'Tax summary' });
    expect(within(tax).getByRole('columnheader', { name: 'CGST' })).toBeInTheDocument();
    expect(within(tax).getAllByText('₹360.00')).toHaveLength(2);                       // 720 split in half
    await userEvent.click(screen.getByRole('button', { name: 'Print' }));
    expect(printSpy).toHaveBeenCalled();
  });

  it('an inter-state invoice prints IGST', async () => {
    at('/sales/invoices/:id/print', <InvoicePrintPage />, '/sales/invoices/i1/print', [perm('sales-invoices', 'VR')], [
      ['GET', '/api/sales-invoices/i1/print', { company: { name: 'Maker Ltd', address: null, state: 'Maharashtra', phone: null, email: null, taxId: null },
        invoice: invoice({ status: 'Submitted', interState: true }) }],
    ]);
    const tax = await screen.findByRole('table', { name: 'Tax summary' });
    expect(within(tax).getByRole('columnheader', { name: 'IGST' })).toBeInTheDocument();
    expect(screen.getByRole('article', { name: 'Tax invoice' })).not.toHaveTextContent('DRAFT');
  });
});

// ---------------------------------------------------------------- Stock details
const row = (over: Partial<StockDetailsRow> = {}): StockDetailsRow => ({
  productId: 'fg', sku: 'FG-1', name: 'Valve', itemType: 'Finished Good', category: null, uom: 'NOS', onHand: 4, stockValue: 600,
  committed: 8, inProduction: 4, available: -4, reorderLevel: 2, status: 'Short', ...over,
});

describe('Stock details screens', () => {
  it('lists on hand, committed and available, and filters items that need attention', async () => {
    const { fetch } = renderScreen(<StockDetailsPage />, [perm('stock-details', 'VR')], [['GET', '/api/stock/details*', paged([row()])]]);
    const r = (await screen.findByRole('link', { name: 'Valve' })).closest('tr')!;
    expect(r).toHaveTextContent('Short');
    expect(r).toHaveTextContent('-4');
    expect(screen.getByRole('link', { name: 'Valve' })).toHaveAttribute('href', '/inventory/stock-details/fg');
    await userEvent.click(screen.getByLabelText(/Needs attention only/));
    await waitFor(() => expect(calls(fetch)).toContain('GET /api/stock/details?attention=true&page=1&pageSize=50'));
  });

  it('one item: stores, P.O. commitments, planned work orders and movements with document links', async () => {
    const details: StockItemDetails = {
      item: { id: 'fg', sku: 'FG-1', name: 'Valve', category: null, unitPrice: 500, stockQuantity: 4, uom: 'NOS', itemType: 'Finished Good',
        hsnCode: '8481', taxRate: 18, standardCost: 120, reorderLevel: 2, leadTimeDays: 7, drawingNo: 'DRG-1', isActive: true, description: 'Forged' },
      summary: row(),
      stores: [{ storeId: 's1', store: 'Main Store', quantity: 4, valuationRate: 150, stockValue: 600 }],
      commitments: [{ customerPoId: 'po1', number: 'CPO-00001', customerName: 'Acme Traders', deliveryDate: null, ordered: 10, produced: 6, invoiced: 2, pending: 8 }],
      openWorkOrders: [{ id: 'w2', number: 'WO-00002', status: 'Draft', date: '2026-11-22', amount: 4, detail: 'CPO-00001' }],
      recentMovements: [{ id: 5, postingDate: '2026-11-22', postedAt: '', productId: 'fg', sku: 'FG-1', productName: 'Valve', storeId: 's1', store: 'Main Store',
        voucherType: 'Sales Invoice', voucherId: 'i1', voucherNo: 'SI-00001', isReversal: false, qtyChange: -2, qtyAfter: 4, rate: 150,
        valuationRate: 150, valueChange: -300, valueAfter: 600 }],
    };
    at('/inventory/stock-details/:productId', <StockItemDetailsPage />, '/inventory/stock-details/fg', [perm('stock-details', 'VR')],
      [['GET', '/api/stock/details/fg', details]]);
    expect(await screen.findByRole('heading', { name: /Valve/ })).toHaveTextContent('Short');
    expect(screen.getByRole('region', { name: 'Item details' })).toHaveTextContent('DRG-1');
    expect(screen.getByRole('table', { name: 'Stock by store' })).toHaveTextContent('Main Store');
    expect(screen.getByRole('link', { name: 'CPO-00001' })).toHaveAttribute('href', '/sales/customer-po/po1');
    expect(screen.getByRole('link', { name: 'WO-00002' })).toHaveAttribute('href', '/production/work-orders/w2');
    expect(screen.getByRole('link', { name: 'SI-00001' })).toHaveAttribute('href', '/sales/invoices/i1');
  });
});

// ---------------------------------------------------------------- Settings › Company
describe('Settings › Company', () => {
  const account = (role: string) => ({ id: 'u1', email: 'a@x.com', fullName: 'A', role, twoFactorEnabled: false, recoveryCodesLeft: 0 });
  const settings = (role: string, routes: Route[] = []) => renderScreen(
    <Routes><RouterRoute path="/settings/:tab?" element={<SettingsPage />} /></Routes>, [],
    [['GET', '/api/account', account(role)], ...routes], '/settings/company');

  it('admins edit the details printed on invoices', async () => {
    const { fetch } = settings('Admin', [
      ['GET', '/api/company', { name: 'Maker Ltd', address: null, state: null, phone: null, email: null, taxId: null }],
      ['PUT', '/api/company', (b: unknown) => b],
    ]);
    const form = await screen.findByRole('form', { name: 'Company' });
    await userEvent.type(within(form).getByLabelText('State'), 'Maharashtra');
    await userEvent.type(within(form).getByLabelText('GSTIN'), '27AAACM0000A1Z1');
    await userEvent.click(within(form).getByRole('button', { name: 'Save company' }));
    expect(await within(form).findByText(/Company details saved/)).toBeInTheDocument();
    expect(body(fetch, 'PUT', '/api/company')).toEqual(
      { name: 'Maker Ltd', address: null, state: 'Maharashtra', phone: null, email: null, taxId: '27AAACM0000A1Z1' });
  });

  it('other roles have no Company tab', async () => {
    settings('User');
    expect(await screen.findByRole('form', { name: 'Profile' })).toBeInTheDocument();       // redirected to the profile
    expect(within(screen.getByRole('navigation', { name: /settings sections/i })).queryByRole('link', { name: 'Company' })).toBeNull();
  });
});
