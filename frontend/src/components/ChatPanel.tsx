import { useRef, useState, type FormEvent } from 'react';
import { api, ApiError, type ChatResponse, type ChatTurn } from '../api/client';
import { ResultTable } from './ResultTable';

interface Message {
  role: 'user' | 'assistant';
  content: string;
  result?: ChatResponse;
  error?: boolean;
}

const SUGGESTIONS = [
  'What is my total revenue this year?',
  'Top 5 products by quantity sold',
  'Which city has the most customers?',
  'Show products with low stock',
];

export function ChatPanel() {
  const [messages, setMessages] = useState<Message[]>([]);
  const [input, setInput] = useState('');
  const [busy, setBusy] = useState(false);
  const endRef = useRef<HTMLDivElement>(null);

  async function send(question: string) {
    const q = question.trim();
    if (!q || busy) return;
    // Send only the last few text turns as context (keeps tokens bounded)
    const history: ChatTurn[] = messages
      .filter((m) => !m.error)
      .slice(-6)
      .map(({ role, content }) => ({ role, content }));

    setMessages((m) => [...m, { role: 'user', content: q }]);
    setInput('');
    setBusy(true);
    try {
      const res = await api.chat(q, history);
      setMessages((m) => [...m, { role: 'assistant', content: res.answer, result: res }]);
    } catch (err) {
      const msg = err instanceof ApiError && err.status === 429
        ? 'You are sending questions too quickly. Please wait a moment.'
        : err instanceof Error ? err.message : 'Something went wrong';
      setMessages((m) => [...m, { role: 'assistant', content: msg, error: true }]);
    } finally {
      setBusy(false);
      queueMicrotask(() => endRef.current?.scrollIntoView?.({ behavior: 'smooth' }));
    }
  }

  function onSubmit(e: FormEvent) {
    e.preventDefault();
    void send(input);
  }

  return (
    <section className="chat card" aria-label="AI assistant">
      <h2>Ask your data</h2>
      <div className="messages" role="log" aria-live="polite">
        {messages.length === 0 && (
          <div className="suggestions">
            {SUGGESTIONS.map((s) => (
              <button key={s} type="button" className="chip" onClick={() => void send(s)}>{s}</button>
            ))}
          </div>
        )}
        {messages.map((m, i) => (
          <div key={i} className={`msg ${m.role} ${m.error ? 'error' : ''}`} data-testid={`msg-${m.role}`}>
            <p>{m.content}</p>
            {m.result && (
              <>
                <ResultTable columns={m.result.columns} rows={m.result.rows} />
                {m.result.sql.length > 0 && (
                  <details>
                    <summary>SQL{m.result.cached ? ' (cached)' : ''}</summary>
                    {m.result.sql.map((s, j) => <pre key={j}>{s}</pre>)}
                  </details>
                )}
              </>
            )}
          </div>
        ))}
        {busy && <div className="msg assistant typing" aria-label="thinking">Thinking…</div>}
        <div ref={endRef} />
      </div>
      <form onSubmit={onSubmit} className="composer">
        <input
          aria-label="question"
          placeholder="e.g. Revenue by month for the last 6 months"
          value={input}
          maxLength={1000}
          onChange={(e) => setInput(e.target.value)}
        />
        <button type="submit" disabled={busy || !input.trim()}>Ask</button>
      </form>
    </section>
  );
}
