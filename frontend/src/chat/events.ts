import { refreshSession, TOKEN_KEY } from '../api/client';
import type { ChatEvent } from './api';

/**
 * Live chat notices for the WEB app: a Server-Sent Events stream read with fetch() (EventSource can't send
 * the Authorization header). The server ends the stream when the access token expires; we reconnect with a
 * fresh token and emit "reconnected" so the screen re-syncs anything it missed.
 * (The desktop app has its own events.ts: there the stream runs in the Electron main process.)
 */
export function subscribeChatEvents(listener: (e: ChatEvent) => void): () => void {
  let stopped = false;
  let ctrl: AbortController | null = null;

  const open = () => fetch('/api/chat/stream', {
    headers: { Authorization: `Bearer ${localStorage.getItem(TOKEN_KEY) ?? ''}`, Accept: 'text/event-stream' },
    credentials: 'same-origin',
    signal: ctrl!.signal,
  });

  void (async () => {
    let everReady = false;
    let failures = 0;
    while (!stopped) {
      ctrl = new AbortController();
      try {
        let res = await open();
        if (res.status === 401 && (await refreshSession())) res = await open();
        if (res.status === 401 || res.status === 403) return; // signed out, or no right to Messages
        if (!res.ok || !res.body) throw new Error(`HTTP ${res.status}`);
        await readSse(res.body, (event, data) => {
          if (event === 'ready') {
            listener({ type: everReady ? 'reconnected' : 'ready' });
            everReady = true;
            failures = 0;
          } else if (event === 'chat') {
            try { listener(JSON.parse(data) as ChatEvent); } catch { /* ignore malformed */ }
          }
        });
      } catch { /* network error or aborted */ }
      if (stopped) return;
      failures++;
      await new Promise((r) => setTimeout(r, Math.min(30_000, 500 * 2 ** Math.min(failures, 6))));
    }
  })();

  return () => {
    stopped = true;
    ctrl?.abort();
  };
}

/** Minimal text/event-stream parser: calls onEvent(event, data) for each dispatched event. */
export async function readSse(body: ReadableStream<Uint8Array>, onEvent: (event: string, data: string) => void): Promise<void> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  let event = 'message';
  let data: string[] = [];
  for (;;) {
    const { value, done } = await reader.read();
    if (done) return;
    buffer += decoder.decode(value, { stream: true });
    let nl: number;
    while ((nl = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, nl).replace(/\r$/, '');
      buffer = buffer.slice(nl + 1);
      if (line === '') {
        if (data.length) onEvent(event, data.join('\n'));
        event = 'message';
        data = [];
      } else if (line.startsWith('event:')) event = line.slice(6).trim();
      else if (line.startsWith('data:')) data.push(line.slice(5).replace(/^ /, ''));
      // ":" comments (heartbeats), "retry:" and "id:" are ignored
    }
  }
}
