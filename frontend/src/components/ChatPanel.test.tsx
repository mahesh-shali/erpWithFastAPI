import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { jsonResponse, renderWithProviders, signIn } from '../test/utils';
import { ChatPanel } from './ChatPanel';

const answer = {
  answer: 'Bengaluru has the most customers (40).',
  sql: ['SELECT * FROM (SELECT city, count(*) FROM customers GROUP BY city) AS _q LIMIT 500'],
  columns: ['city', 'count'],
  rows: [['Bengaluru', 40], ['Mumbai', 40]],
  cached: false,
  latency_ms: 900,
};

describe('ChatPanel', () => {
  it('sends a question and renders answer, table and SQL', async () => {
    signIn();
    const f = vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse(answer));
    renderWithProviders(<ChatPanel />);

    await userEvent.type(screen.getByLabelText('question'), 'Which city has the most customers?');
    await userEvent.click(screen.getByRole('button', { name: 'Ask' }));

    expect(await screen.findByText(answer.answer)).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: 'city' })).toBeInTheDocument();
    expect(screen.getAllByRole('row')).toHaveLength(3);
    expect(screen.getByText(/LIMIT 500/)).toBeInTheDocument();

    const [url, init] = f.mock.calls[0];
    expect(url).toBe('/ai/chat');
    expect(JSON.parse(init!.body as string)).toEqual({ question: 'Which city has the most customers?', history: [] });
  });

  it('sends prior turns as history on follow-up questions', async () => {
    signIn();
    const f = vi.spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(jsonResponse(answer))
      .mockResolvedValueOnce(jsonResponse({ ...answer, answer: 'Mumbai is second.' }));
    renderWithProviders(<ChatPanel />);

    await userEvent.type(screen.getByLabelText('question'), 'Top city?');
    await userEvent.click(screen.getByRole('button', { name: 'Ask' }));
    await screen.findByText(answer.answer);
    await userEvent.type(screen.getByLabelText('question'), 'And second?');
    await userEvent.click(screen.getByRole('button', { name: 'Ask' }));
    await screen.findByText('Mumbai is second.');

    const body = JSON.parse(f.mock.calls[1][1]!.body as string);
    expect(body.history).toEqual([
      { role: 'user', content: 'Top city?' },
      { role: 'assistant', content: answer.answer },
    ]);
  });

  it('shows a friendly message on 429', async () => {
    signIn();
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse({ detail: 'Rate limit exceeded' }, 429));
    renderWithProviders(<ChatPanel />);
    await userEvent.click(screen.getByRole('button', { name: /top 5 products/i }));
    await waitFor(() => expect(screen.getByText(/too quickly/i)).toBeInTheDocument());
  });

  it('disables Ask for empty input', () => {
    signIn();
    renderWithProviders(<ChatPanel />);
    expect(screen.getByRole('button', { name: 'Ask' })).toBeDisabled();
  });
});
