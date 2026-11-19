import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { PageRights } from '../api/client';
import { RightsMatrix, type MatrixRow } from './RightsMatrix';

const none: PageRights = { isVisible: false, isRestricted: false, canRead: false, canWrite: false, canUpdate: false, canDelete: false };
const read: PageRights = { ...none, isVisible: true, canRead: true };

const rows: MatrixRow[] = [
  { pageId: 3, pageName: 'Products', section: 'Master › Products', rights: read, isCustom: false, defaultRights: read },
  { pageId: 6, pageName: 'Users', section: 'Master › Users', rights: { ...read, canWrite: true }, isCustom: true, defaultRights: none },
];

describe('RightsMatrix', () => {
  it('saves only changed rows: a toggled default row becomes an override', async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);
    render(<RightsMatrix rows={rows} editable customLabel="Overridden" resetLabel="Use role default" onSave={onSave} />);

    await userEvent.click(screen.getByLabelText('Products Write'));
    await userEvent.click(screen.getByRole('button', { name: /save changes \(1\)/i }));

    expect(onSave).toHaveBeenCalledWith([{ pageId: 3, useDefault: false, rights: { ...read, canWrite: true } }]);
    expect(await screen.findByRole('status')).toHaveTextContent('Saved');
  });

  it('reset sends useDefault and shows the default rights', async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);
    render(<RightsMatrix rows={rows} editable customLabel="Overridden" resetLabel="Use role default" onSave={onSave} />);

    await userEvent.click(screen.getByRole('button', { name: 'Use role default' }));
    expect(screen.getByLabelText('Users Write')).not.toBeChecked();
    await userEvent.click(screen.getByRole('button', { name: /save changes/i }));

    expect(onSave).toHaveBeenCalledWith([{ pageId: 6, useDefault: true, rights: null }]);
  });

  it('is read-only when not editable', () => {
    render(<RightsMatrix rows={rows} editable={false} customLabel="Overridden" resetLabel="Reset" onSave={vi.fn()} />);
    expect(screen.getByLabelText('Products Read')).toBeDisabled();
    expect(screen.queryByRole('button', { name: /save/i })).not.toBeInTheDocument();
  });

  it('shows the server error when saving is refused', async () => {
    const onSave = vi.fn().mockRejectedValue(new Error("You can't grant rights on 'products' that you don't have yourself."));
    render(<RightsMatrix rows={rows} editable customLabel="Overridden" resetLabel="Reset" onSave={onSave} />);
    await userEvent.click(screen.getByLabelText('Products Delete'));
    await userEvent.click(screen.getByRole('button', { name: /save changes/i }));
    expect(await screen.findByRole('alert')).toHaveTextContent("can't grant rights");
  });
});
