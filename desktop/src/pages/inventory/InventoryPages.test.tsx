import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import type { StockEntry, StockLedgerRow, StockPosition, Store, Uom } from '../../api/client';
import { calls, jsonResponse, mockApi, perm, renderScreen, type Route as ApiRoute } from '../../test/utils';
import { ProductsPage } from '../DataPages';
import { StoresPage, UomsPage } from './MasterPages';
import { StockEntriesPage, StockEntryPage } from './StockEntryPages';
import { StockLedgerPage, StockPositionPage } from './StockReportPages';

const body = (spy: ReturnType<typeof mockApi>, method: string, url: string) =>
  JSON.parse(String(spy.mock.calls.find(([u, i]) => String(u) === url && i?.method === method)![1]!.body));

const uoms: Uom[] = [{ id: 'u1', code: 'NOS', name: 'Numbers', isActive: true }, { id: 'u2', code: 'BOX', name: 'Box', isActive: false }];
const stores: Store[] = [
  { id: 's1', code: 'MAIN', name: 'Main Store', location: null, isDefault: true, isActive: true },
  { id: 's2', code: 'WH', name: 'Warehouse', location: 'Pune', isDefault: false, isActive: true },
];
const items = [
  { id: 'p1', sku: 'SKU-1', name: 'Widget', uom: 'NOS', stockQuantity: 40, unitPrice: 120 },
  { id: 'p2', sku: 'SKU-2', name: 'Gadget', uom: null, stockQuantity: 0, unitPrice: 50 },
];
const LOOKUPS: ApiRoute[] = [['GET', '/api/stock/stores', stores], ['GET', '/api/stock/items', items], ['GET', '/api/stock/uoms', uoms]];

describe('Units of measure screen', () => {
  it('lists units, adds one and shows why a unit in use cannot be deleted', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    let list = uoms;
    const { fetch } = renderScreen(<UomsPage />, [perm('uom', 'VRWUD')], [
      ['GET', '/api/uoms?all=true', () => list],
      ['POST', '/api/uoms', (b: unknown) => { list = [...uoms, { id: 'u3', ...(b as Omit<Uom, 'id'>), code: 'KG' }]; return list[2]; }],
      ['DELETE', '/api/uoms/u1', jsonResponse({ title: 'This unit is used by products. Deactivate it instead.' }, 409)],
    ]);
    expect(await screen.findByText('Numbers')).toBeInTheDocument();
    expect(screen.getByText('BOX').closest('tr')).toHaveTextContent('Inactive');

    await userEvent.click(screen.getByRole('button', { name: 'Add unit' }));
    const form = screen.getByRole('form', { name: 'Add unit' });
    await userEvent.type(within(form).getByLabelText('Code'), 'kg');
    await userEvent.type(within(form).getByLabelText('Name'), 'Kilogram');
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('Kilogram')).toBeInTheDocument();
    expect(body(fetch, 'POST', '/api/uoms')).toEqual({ code: 'kg', name: 'Kilogram', isActive: true });

    await userEvent.click(within(screen.getByText('NOS').closest('tr')!).getByRole('button', { name: 'Delete' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('used by products');
  });

  it('is read-only without write/update/delete rights', async () => {
    renderScreen(<UomsPage />, [perm('uom', 'VR')], [['GET', '/api/uoms?all=true', uoms]]);
    await screen.findByText('Numbers');
    expect(screen.queryByRole('button', { name: 'Add unit' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();
  });
});

describe('Stores screen', () => {
  it('marks the default store, which cannot be deleted, and edits a store', async () => {
    const { fetch } = renderScreen(<StoresPage />, [perm('stores', 'VRWUD')], [
      ['GET', '/api/stores?all=true', stores],
      ['PUT', '/api/stores/s2', (b: unknown) => ({ ...stores[1], ...(b as object) })],
    ]);
    const main = (await screen.findByText('Main Store')).closest('tr')!;
    expect(main).toHaveTextContent('Default');
    expect(within(main).queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();

    const wh = screen.getByText('Warehouse').closest('tr')!;
    await userEvent.click(within(wh).getByRole('button', { name: 'Edit' }));
    const form = screen.getByRole('form', { name: 'Edit store' });
    await userEvent.clear(within(form).getByLabelText('Location'));
    await userEvent.type(within(form).getByLabelText('Location'), 'Mumbai');
    await userEvent.click(within(form).getByLabelText('Default store'));
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(calls(fetch)).toContain('PUT /api/stores/s2'));
    expect(body(fetch, 'PUT', '/api/stores/s2')).toEqual(
      { code: 'WH', name: 'Warehouse', location: 'Mumbai', isActive: true, isDefault: true });
  });
});

const entry = (over: Partial<StockEntry> = {}): StockEntry => ({
  id: 'e1', number: 'SE-00001', type: 'Receipt', status: 'Draft', postingDate: '2026-11-16',
  sourceStoreId: null, sourceStore: null, targetStoreId: 's1', targetStore: 'Main Store', remarks: null,
  totalValue: 1200, createdAt: '2026-11-16T10:00:00Z', submittedAt: null, cancelledAt: null,
  lines: [{ productId: 'p1', sku: 'SKU-1', productName: 'Widget', uom: 'NOS', quantity: 10, rate: 120, amount: 1200 }],
  ...over,
});

function renderEntry(path: string, rights: string, routes: ApiRoute[]) {
  return renderScreen(
    <Routes>
      <Route path="/inventory/stock-addition" element={<h1>List</h1>} />
      <Route path="/inventory/stock-addition/:id" element={<StockEntryPage />} />
    </Routes>,
    [perm('stock-addition', rights)], [...LOOKUPS, ...routes], path);
}

describe('Stock entries screen', () => {
  it('lists entries with status and value, and filters by status', async () => {
    const { fetch } = renderScreen(<StockEntriesPage />, [perm('stock-addition', 'VRW')], [['GET', '/api/stock-entries*', {
      items: [{ id: 'e1', number: 'SE-00001', type: 'Receipt', status: 'Submitted', postingDate: '2026-11-16',
        sourceStore: null, targetStore: 'Main Store', lineCount: 2, totalValue: 1200, createdAt: '' }],
      page: 1, pageSize: 25, total: 1,
    }]]);
    const row = (await screen.findByRole('link', { name: 'SE-00001' })).closest('tr')!;
    expect(row).toHaveTextContent('Submitted');
    expect(row).toHaveTextContent('₹1,200.00');
    expect(screen.getByRole('link', { name: 'New stock entry' })).toHaveAttribute('href', '/inventory/stock-addition/new');

    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Status' }), 'Draft');
    await waitFor(() => expect(calls(fetch)).toContain('GET /api/stock-entries?status=Draft&page=1&pageSize=25'));
  });

  it('creates a receipt draft with the item price as the default rate', async () => {
    const { fetch } = renderEntry('/inventory/stock-addition/new', 'VRWUD', [['POST', '/api/stock-entries', entry()], ['GET', '/api/stock-entries/e1', entry()]]);
    await screen.findByRole('option', { name: 'SKU-1 · Widget' });
    expect(screen.getByRole('combobox', { name: 'To store' })).toHaveValue('s1');          // default store preselected
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Item 1' }), 'p1');
    expect(screen.getByRole('spinbutton', { name: 'Rate 1' })).toHaveValue(120);
    await userEvent.clear(screen.getByRole('spinbutton', { name: 'Quantity 1' }));
    await userEvent.type(screen.getByRole('spinbutton', { name: 'Quantity 1' }), '10');
    expect(screen.getByRole('table', { name: 'Items' })).toHaveTextContent('₹1,200.00');

    await userEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    expect(await screen.findByRole('heading', { name: /SE-00001/ })).toBeInTheDocument();
    expect(body(fetch, 'POST', '/api/stock-entries')).toMatchObject({
      type: 'Receipt', sourceStoreId: null, targetStoreId: 's1', lines: [{ productId: 'p1', quantity: 10, rate: 120 }],
    });
  });

  it('an issue has a From store and no rate', async () => {
    renderEntry('/inventory/stock-addition/new', 'VRW', []);
    await screen.findByRole('option', { name: 'SKU-1 · Widget' });
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Type' }), 'Issue');
    expect(screen.getByRole('combobox', { name: 'From store' })).toBeInTheDocument();
    expect(screen.queryByRole('combobox', { name: 'To store' })).not.toBeInTheDocument();
    expect(screen.queryByRole('spinbutton', { name: 'Rate 1' })).not.toBeInTheDocument();
    expect(screen.getByText('at moving average')).toBeInTheDocument();
  });

  it('submits a draft and shows the server error when stock is short', async () => {
    renderEntry('/inventory/stock-addition/e1', 'VRWUD', [
      ['GET', '/api/stock-entries/e1', entry({ type: 'Issue', sourceStoreId: 's1', sourceStore: 'Main Store', targetStoreId: null })],
      ['POST', '/api/stock-entries/e1/submit', jsonResponse({ title: 'Insufficient stock for SKU-1 in Main Store: 4 available, 10 needed.' }, 400)],
    ]);
    await userEvent.click(await screen.findByRole('button', { name: 'Submit' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('4 available, 10 needed');
  });

  it('a submitted entry is read-only and can be cancelled with the delete right', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const submitted = entry({ status: 'Submitted', submittedAt: '2026-11-16T11:00:00Z' });
    const { fetch } = renderEntry('/inventory/stock-addition/e1', 'VRWUD', [
      ['GET', '/api/stock-entries/e1', submitted],
      ['POST', '/api/stock-entries/e1/cancel', entry({ status: 'Cancelled', cancelledAt: '2026-11-16T12:00:00Z' })],
    ]);
    expect(await screen.findByText('Submitted', { selector: '.status' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save draft' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Submit' })).not.toBeInTheDocument();
    expect(screen.getByText('SKU-1 · Widget')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Cancel entry' }));
    expect(await screen.findByRole('status')).toHaveTextContent('reversed');
    expect(screen.getByText('Cancelled', { selector: '.status' })).toBeInTheDocument();
    expect(calls(fetch)).toContain('POST /api/stock-entries/e1/cancel');
  });

  it('without Update a draft is read-only and cannot be submitted', async () => {
    renderEntry('/inventory/stock-addition/e1', 'VRW', [['GET', '/api/stock-entries/e1', entry()]]);
    await screen.findByRole('heading', { name: /SE-00001/ });
    expect(screen.queryByRole('button', { name: 'Submit' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save draft' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Delete draft' })).not.toBeInTheDocument();
  });

  it('deletes a draft and returns to the list', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    renderEntry('/inventory/stock-addition/e1', 'VRWUD', [
      ['GET', '/api/stock-entries/e1', entry()], ['DELETE', '/api/stock-entries/e1', undefined],
    ]);
    await userEvent.click(await screen.findByRole('button', { name: 'Delete draft' }));
    expect(await screen.findByRole('heading', { name: 'List' })).toBeInTheDocument();
  });
});

const position: StockPosition = {
  items: [
    { productId: 'p1', sku: 'SKU-1', name: 'Widget', category: 'Tools', uom: 'NOS', storeId: 's1', store: 'Main Store', quantity: 40, valuationRate: 113.3333, stockValue: 4533.33 },
    { productId: 'p2', sku: 'SKU-2', name: 'Gadget', category: null, uom: null, storeId: 's2', store: 'Warehouse', quantity: 3, valuationRate: 50, stockValue: 150 },
  ],
  page: 1, pageSize: 50, total: 2, totalQuantity: 43, totalValue: 4683.33,
};

describe('Stock position screen', () => {
  it('shows totals, flags low stock and filters', async () => {
    const { fetch } = renderScreen(<StockPositionPage />, [perm('stock-position')], [...LOOKUPS, ['GET', '/api/stock/position*', position]]);
    const totals = await screen.findByRole('region', { name: 'Totals' });
    expect(totals).toHaveTextContent('43');
    expect(totals).toHaveTextContent('₹4,683.33');
    expect(screen.getByText('Gadget').closest('tr')).toHaveTextContent('Low');
    expect(screen.getByText('Widget').closest('tr')).not.toHaveTextContent('Low');
    expect(within(screen.getByText('Widget').closest('tr')!).getByRole('link', { name: 'Ledger' }))
      .toHaveAttribute('href', '/reports/stock?productId=p1&storeId=s1');

    await userEvent.click(screen.getByLabelText(/Low stock only/));
    await waitFor(() => expect(calls(fetch)).toContain('GET /api/stock/position?lowOnly=true&page=1&pageSize=50'));
    await screen.findByRole('option', { name: 'Warehouse' });
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Store' }), 's2');
    await waitFor(() => expect(calls(fetch).some((c) => c.includes('storeId=s2'))).toBe(true));
  });
});

const ledger: StockLedgerRow[] = [
  { id: 2, postingDate: '2026-11-16', postedAt: '', productId: 'p1', sku: 'SKU-1', productName: 'Widget', storeId: 's1', store: 'Main Store',
    voucherType: 'Stock Entry', voucherId: 'e1', voucherNo: 'SE-00001', isReversal: false, qtyChange: -5, qtyAfter: 35,
    rate: 113.3333, valuationRate: 113.3333, valueChange: -566.67, valueAfter: 3966.66 },
  { id: 1, postingDate: '2026-11-15', postedAt: '', productId: 'p1', sku: 'SKU-1', productName: 'Widget', storeId: 's1', store: 'Main Store',
    voucherType: 'Opening', voucherId: null, voucherNo: 'OPENING', isReversal: false, qtyChange: 40, qtyAfter: 40,
    rate: 113.3333, valuationRate: 113.3333, valueChange: 4533.33, valueAfter: 4533.33 },
];

describe('Stock ledger screen', () => {
  it('lists movements newest first with links to their stock entry, and reads filters from the URL', async () => {
    const { fetch } = renderScreen(<StockLedgerPage />, [perm('stock-report')],
      [...LOOKUPS, ['GET', '/api/stock/ledger*', { items: ledger, page: 1, pageSize: 50, total: 2 }]], '/?productId=p1');
    await screen.findByRole('link', { name: 'SE-00001' });
    const rows = screen.getAllByRole('row').slice(1);
    expect(rows[0]).toHaveTextContent('-5');
    expect(within(rows[0]).getByRole('link', { name: 'SE-00001' })).toHaveAttribute('href', '/inventory/stock-addition/e1');
    expect(rows[1]).toHaveTextContent('Opening');
    expect(rows[1]).not.toHaveTextContent('OPENING');
    expect(rows[1]).toHaveTextContent('+40');
    expect(calls(fetch)).toContain('GET /api/stock/ledger?productId=p1&page=1&pageSize=50');
  });
});

describe('Items screen: add with unit', () => {
  it('adds a product with opening stock and a unit', async () => {
    let list = [{ id: 'p1', sku: 'SKU-1', name: 'Widget', category: 'Tools', unitPrice: 120, stockQuantity: 40, uomId: 'u1', uom: 'NOS' }];
    const { fetch } = renderScreen(<ProductsPage />, [perm('products', 'VRWUD')], [
      ['GET', '/api/products*', () => ({ items: list, page: 1, pageSize: 25, total: list.length })],
      ['GET', '/api/stock/uoms', uoms],
      ['POST', '/api/products', (b: unknown) => { list = [...list, { ...(b as typeof list[0]), id: 'p9', uom: 'NOS' }]; return list[1]; }],
    ]);
    expect((await screen.findByText('Widget')).closest('tr')).toHaveTextContent('NOS');
    await userEvent.click(screen.getByRole('button', { name: 'Add item' }));
    const form = screen.getByRole('form', { name: 'Add item' });
    await userEvent.type(within(form).getByLabelText('SKU'), 'SKU-9');
    await userEvent.type(within(form).getByLabelText('Name'), 'Bolt');
    await screen.findByRole('option', { name: 'NOS · Numbers' });
    await userEvent.selectOptions(within(form).getByRole('combobox', { name: 'Unit' }), 'u1');
    await userEvent.type(within(form).getByLabelText('Price'), '5');
    await userEvent.clear(within(form).getByLabelText('Opening stock'));
    await userEvent.type(within(form).getByLabelText('Opening stock'), '100');
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('Bolt')).toBeInTheDocument();
    expect(body(fetch, 'POST', '/api/products')).toEqual(
      { sku: 'SKU-9', name: 'Bolt', category: null, unitPrice: 5, stockQuantity: 100, uomId: 'u1', itemType: 'Finished Good',
        description: null, hsnCode: null, taxRate: 18, standardCost: 0, reorderLevel: 0, leadTimeDays: 0, drawingNo: null, isActive: true });
  });
});
