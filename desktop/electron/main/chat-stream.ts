import type { ChatStreamEvent } from '../shared/contract';

/** What ChatStream needs from ApiClient (see ApiClient.openStream). */
export interface StreamOpener {
  openStream(path: string, signal: AbortSignal): Promise<{ status: number; body: ReadableStream<Uint8Array> | null }>;
}

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * Validates a server event before it crosses into the renderer: only known types, ids of the right shape,
 * nothing else (the server sends notices only; message content is fetched and decrypted by the renderer).
 */
export function sanitizeChatEvent(data: string): ChatStreamEvent | null {
  let raw: unknown;
  try { raw = JSON.parse(data); } catch { return null; }
  if (!raw || typeof raw !== 'object') return null;
  const { type, conversationId, messageId } = raw as Record<string, unknown>;
  if (type !== 'message' && type !== 'conversation') return null;
  if (conversationId != null && (typeof conversationId !== 'string' || !UUID.test(conversationId))) return null;
  if (messageId != null && (typeof messageId !== 'number' || !Number.isSafeInteger(messageId) || messageId < 0)) return null;
  return { type, conversationId: (conversationId as string | null | undefined) ?? null, messageId: (messageId as number | null | undefined) ?? null };
}

/** Minimal text/event-stream parser; resolves when the stream ends. */
export async function readSse(body: ReadableStream<Uint8Array>, onEvent: (event: string, data: string) => void): Promise<void> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  let event = 'message';
  let data: string[] = [];
  try {
    for (;;) {
      const { value, done } = await reader.read();
      if (done) return;
      buffer += decoder.decode(value, { stream: true });
      if (buffer.length > 1_000_000) throw new Error('Event too large');
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
      }
    }
  } finally {
    reader.releaseLock();
  }
}

const abortableSleep = (ms: number, signal: AbortSignal) =>
  new Promise<void>((resolve) => {
    const t = setTimeout(resolve, ms);
    signal.addEventListener('abort', () => { clearTimeout(t); resolve(); }, { once: true });
  });

/**
 * Holds the Messages live stream (GET /api/chat/stream) in the main process, so the renderer keeps
 * having no network access and never sees a token. The API ends the stream when the access token expires;
 * we reconnect (ApiClient refreshes the token) and emit "reconnected" so the screen re-syncs.
 */
export class ChatStream {
  private ctrl: AbortController | null = null;

  constructor(
    private readonly api: StreamOpener,
    private readonly emit: (event: ChatStreamEvent) => void,
    private readonly sleep: (ms: number, signal: AbortSignal) => Promise<void> = abortableSleep,
  ) {}

  get running(): boolean {
    return this.ctrl !== null;
  }

  start(): void {
    if (this.ctrl) return;
    const ctrl = new AbortController();
    this.ctrl = ctrl;
    void this.loop(ctrl);
  }

  stop(): void {
    this.ctrl?.abort();
    this.ctrl = null;
  }

  private async loop(ctrl: AbortController) {
    const { signal } = ctrl;
    let everReady = false;
    let failures = 0;
    while (!signal.aborted) {
      try {
        const res = await this.api.openStream('/api/chat/stream', signal);
        if (res.status === 401 || res.status === 403) break; // signed out, or no right to Messages
        if (!res.body) throw new Error(`HTTP ${res.status}`);
        await readSse(res.body, (event, data) => {
          if (signal.aborted) return;
          if (event === 'ready') {
            this.emit({ type: everReady ? 'reconnected' : 'ready' });
            everReady = true;
            failures = 0;
          } else if (event === 'chat') {
            const e = sanitizeChatEvent(data);
            if (e) this.emit(e);
          }
        });
      } catch {
        /* network error, or aborted by stop() */
      }
      if (signal.aborted) break;
      failures++;
      await this.sleep(Math.min(30_000, 500 * 2 ** Math.min(failures, 6)), signal);
    }
    if (this.ctrl === ctrl) this.ctrl = null; // a newer start() owns this.ctrl otherwise
  }
}
