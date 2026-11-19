import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { emitUpdate, fakeState } from '../test/fakeBridge';
import { UpdateButton } from './UpdateButton';

describe('UpdateButton', () => {
  it('shows a dot when a new version is out, and downloads it on request', async () => {
    render(<UpdateButton />);
    act(() => emitUpdate({ state: 'available', version: '1.2.0', releaseNotes: 'Faster orders' }));

    const btn = screen.getByRole('button', { name: 'Update available: version 1.2.0' });
    await userEvent.click(btn);
    expect(screen.getByRole('dialog', { name: 'App updates' })).toHaveTextContent('Installed version 0.0.0');
    expect(screen.getByText('Faster orders')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Download update' }));
    expect(fakeState.updateCalls).toContain('download');
    expect(await screen.findByRole('progressbar', { name: 'Download progress' })).toBeInTheDocument();

    act(() => emitUpdate({ state: 'downloading', version: '1.2.0', percent: 64 }));
    expect(screen.getByRole('button', { name: 'Downloading update: 64%' })).toBeInTheDocument();
  });

  it('offers Restart now once downloaded', async () => {
    render(<UpdateButton />);
    act(() => emitUpdate({ state: 'downloaded', version: '1.2.0' }));
    await userEvent.click(screen.getByRole('button', { name: 'Update ready: version 1.2.0' }));
    await userEvent.click(screen.getByRole('button', { name: 'Restart now' }));
    expect(fakeState.updateCalls).toContain('install');
  });

  it('checks on request and closes with Escape', async () => {
    render(<UpdateButton />);
    await userEvent.click(screen.getByRole('button', { name: 'Updates' }));
    await userEvent.click(screen.getByRole('button', { name: 'Check for updates' }));
    expect(fakeState.updateCalls).toEqual(['check']);
    await userEvent.keyboard('{Escape}');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('explains when updates are not available (dev run)', async () => {
    fakeState.update = { state: 'unsupported', reason: 'Updates are available in the installed app.' };
    render(<UpdateButton />);
    await userEvent.click(await screen.findByRole('button', { name: 'Updates' }));
    expect(await screen.findByText('Updates are available in the installed app.')).toBeInTheDocument();
  });
});
