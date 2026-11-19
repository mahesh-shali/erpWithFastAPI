import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { calls, jsonResponse, perm, renderScreen } from '../test/utils';
import { AssistantPage, CustomersPage, OrdersPage, ProductsPage } from './DataPages';

const customers = [
  { id: 'c1', name: 'Acme Traders', email: 'acme@x.com', city: 'Pune', country: 'India' },
  { id: 'c2', name: 'No Contact Ltd', email: null, city: null, country: null },
];
const products = [
  { id: 'p1', sku: 'SKU-1', name: 'Widget', category: 'Tools', unitPrice: 1234.5, stockQuantity: 7 },
  { id: 'p2', sku: 'SKU-2', name: 'Gadget', category: null, unitPrice: 99, stockQuantity: 0 },
];
const paged = <T,>(items: T[]) => ({ items, page: 1, pageSize: 25, total: items.length });

describe('Customers screen', () => {
  it('lists customers with a dash for missing fields', async () => {
    renderScreen(<CustomersPage />, [perm('customers', 'VR')], [['GET', '/api/customers*', paged(customers)]]);

    const table = await screen.findByRole('table', { name: 'Customers' });
    expect(await within(table).findByText('Acme Traders')).toBeInTheDocument();
    const row = within(table).getByText('No Contact Ltd').closest('tr')!;
    expect(within(row).getAllByText('—')).toHaveLength(3);
    expect(screen.getByText('Showing the first 25.')).toBeInTheDocument();
  });

  it('hides Delete without the delete right', async () => {
    renderScreen(<CustomersPage />, [perm('customers', 'VRWU')], [['GET', '/api/customers*', paged(customers)]]);
    await screen.findByText('Acme Traders');
    expect(screen.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();
  });

  it('deletes after confirmation and reloads the list', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    let list = customers;
    const { fetch } = renderScreen(<CustomersPage />, [perm('customers', 'VRWUD')], [
      ['GET', '/api/customers*', () => paged(list)],
      ['DELETE', '/api/customers/c1', () => { list = customers.slice(1); return undefined; }],
    ]);

    await userEvent.click((await screen.findAllByRole('button', { name: 'Delete' }))[0]);

    await waitFor(() => expect(screen.queryByText('Acme Traders')).not.toBeInTheDocument());
    expect(calls(fetch)).toContain('DELETE /api/customers/c1');
  });

  it('does nothing when the confirmation is cancelled', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(false);
    const { fetch } = renderScreen(<CustomersPage />, [perm('customers', 'VRWUD')],
      [['GET', '/api/customers*', paged(customers)]]);
    await userEvent.click((await screen.findAllByRole('button', { name: 'Delete' }))[0]);
    expect(calls(fetch).some((c) => c.startsWith('DELETE'))).toBe(false);
  });

  it('shows the server error when delete is refused', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    renderScreen(<CustomersPage />, [perm('customers', 'VRWUD')], [
      ['GET', '/api/customers*', paged(customers)],
      ['DELETE', '/api/customers/c1', jsonResponse({ title: 'Customer has orders' }, 409)],
    ]);
    await userEvent.click((await screen.findAllByRole('button', { name: 'Delete' }))[0]);
    expect(await screen.findByRole('alert')).toHaveTextContent('Customer has orders');
  });

  it('shows the load error', async () => {
    renderScreen(<CustomersPage />, [perm('customers', 'VR')],
      [['GET', '/api/customers*', jsonResponse({ title: 'Forbidden' }, 403)]]);
    expect(await screen.findByRole('alert')).toHaveTextContent('Forbidden');
  });
});

describe('Products screen', () => {
  it('lists products with price in rupees and stock', async () => {
    renderScreen(<ProductsPage />, [perm('products', 'VR')], [['GET', '/api/products*', paged(products)]]);
    const row = (await screen.findByText('Widget')).closest('tr')!;
    expect(row).toHaveTextContent('SKU-1');
    expect(row).toHaveTextContent('Tools');
    expect(row).toHaveTextContent('₹1,234.50');
    expect(row).toHaveTextContent('7');
    expect(screen.getByText('Gadget').closest('tr')).toHaveTextContent('—');
    expect(screen.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();
  });

  it('deletes a product with the delete right', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    let list = products;
    const { fetch } = renderScreen(<ProductsPage />, [perm('products', 'VRWUD')], [
      ['GET', '/api/products*', () => paged(list)],
      ['DELETE', '/api/products/p2', () => { list = products.slice(0, 1); return undefined; }],
    ]);
    await screen.findByText('Gadget');
    await userEvent.click(screen.getAllByRole('button', { name: 'Delete' })[1]);
    await waitFor(() => expect(screen.queryByText('Gadget')).not.toBeInTheDocument());
    expect(calls(fetch)).toContain('DELETE /api/products/p2');
  });
});

describe('Orders screen', () => {
  it('lists the latest orders with status, item count and total', async () => {
    renderScreen(<OrdersPage />, [perm('orders', 'VR')], [['GET', '/api/orders?limit=25', [
      { id: 'o1', customerId: 'c1', status: 'Paid', totalAmount: 2500, orderedAt: '2026-11-10T10:00:00Z', items: [{}, {}] },
    ]]]);
    const row = (await screen.findByText('Paid')).closest('tr')!;
    expect(row).toHaveTextContent('₹2,500.00');
    expect(within(row).getAllByRole('cell')[2]).toHaveTextContent('2');
    expect(screen.getByText('Latest 25 orders.')).toBeInTheDocument();
  });

  it('shows the load error', async () => {
    renderScreen(<OrdersPage />, [perm('orders', 'VR')],
      [['GET', '/api/orders?limit=25', jsonResponse({ title: 'Server error' }, 500)]]);
    expect(await screen.findByRole('alert')).toHaveTextContent('Server error');
  });
});

describe('AI Assistant screen', () => {
  it('renders the chat panel', async () => {
    renderScreen(<AssistantPage />, [perm('ai-chat', 'VR')]);
    expect(screen.getByRole('heading', { name: 'AI Assistant' })).toBeInTheDocument();
    expect(screen.getByLabelText('question')).toBeInTheDocument();
  });
});
