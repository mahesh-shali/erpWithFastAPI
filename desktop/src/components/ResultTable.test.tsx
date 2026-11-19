import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ResultTable } from './ResultTable';

describe('ResultTable', () => {
  it('renders nothing without columns or rows', () => {
    const { container: a } = render(<ResultTable columns={[]} rows={[[1]]} />);
    const { container: b } = render(<ResultTable columns={['city']} rows={[]} />);
    expect(a).toBeEmptyDOMElement();
    expect(b).toBeEmptyDOMElement();
  });

  it('renders headers and cells, with a dash for NULL', () => {
    render(<ResultTable columns={['city', 'count']} rows={[['Pune', 3], [null, 0]]} />);
    expect(screen.getAllByRole('columnheader').map((h) => h.textContent)).toEqual(['city', 'count']);
    expect(screen.getAllByRole('row')[2]).toHaveTextContent('—0');
  });

  it('shows at most 50 rows and says how many there are', () => {
    const rows = Array.from({ length: 120 }, (_, i) => [i]);
    render(<ResultTable columns={['n']} rows={rows} />);
    expect(screen.getAllByRole('row')).toHaveLength(51); // header + 50
    expect(screen.getByText('Showing 50 of 120 rows')).toBeInTheDocument();
  });
});
