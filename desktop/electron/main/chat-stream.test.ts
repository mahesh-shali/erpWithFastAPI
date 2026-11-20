import { describe, expect, it, vi } from 'vitest';
import type { ChatStreamEvent } from '../shared/contract';
import { ChatStream, readSse, sanitizeChatEvent, type StreamOpener } from './chat-stream';

const CONV = '3f2504e0-4f89-41d3-9a0c-0305e82c3301';

function sse(text: string, keepOpen = false) {
  const enc = new TextEncoder();
  return new ReadableStream<Uint8Array>({
    start(c) {
      c.enqueue(enc.encode(text));
      if (!keepOpen) c.close();
    },
  });
}

describe('sanitizeChatEvent', () => {
  it('passes known notices through', () => {
    expect(sanitizeChatEvent(`{"type":"message","conversationId":"${CONV}","messageId":42}`))
      .toEqual({ type: 'message', conversationId: CONV, messageId: 42 });
    expect(sanitizeChatEvent('{"type":"conversation"}')).toEqual({ type: 'conversation', conversationId: null, messageId: null });
  });

  it.each([
    'not json',
    '{"type":"ready"}',
    '{"type":"message","conversationId":"../../etc"}',
    '{"type":"message","messageId":"7"}',
    '{"type":"message","messageId":-1}',
    'null',
  ])('drops %s', (data) => expect(sanitizeChatEvent(data)).toBeNull());

  it('never forwards extra fields', () => {
    expect(sanitizeChatEvent(`{"type":"message","conversationId":"${CONV}","ciphertext":"x","token":"y"}`))
      .toEqual({ type: 'message', conversationId: CONV, messageId: null });
  });
});

describe('readSse', () => {
  it('dispatches events and ignores comments', async () => {
    const got: string[] = [];
    await readSse(sse('event: ready\ndata: {}\n\n: ping\n\nevent: chat\ndata: {"a":1}\n\n'), (e, d) => got.push(`${e}:${d}`));
    expect(got).toEqual(['ready:{}', 'chat:{"a":1}']);
  });
});

describe('ChatStream', () => {
  const noSleep = async () => undefined;

  it('forwards validated events, reconnects after the stream ends and says so', async () => {
    const events: ChatStreamEvent[] = [];
    let opened = 0;
    let stream: ChatStream;
    const api: StreamOpener = {
      openStream: vi.fn(async () => {
        opened++;
        if (opened === 1) return { status: 200, body: sse(`event: ready\ndata: {}\n\nevent: chat\ndata: {"type":"message","conversationId":"${CONV}","messageId":1}\n\nevent: chat\ndata: {"type":"evil"}\n\n`) };
        if (opened === 2) return { status: 200, body: sse('event: ready\ndata: {}\n\n', true) };
        throw new Error('unexpected');
      }),
    };
    stream = new ChatStream(api, (e) => events.push(e), noSleep);
    stream.start();
    stream.start(); // idempotent
    await vi.waitFor(() => expect(events).toHaveLength(3));
    expect(events).toEqual([
      { type: 'ready' },
      { type: 'message', conversationId: CONV, messageId: 1 },
      { type: 'reconnected' },
    ]);
    expect(api.openStream).toHaveBeenCalledWith('/api/chat/stream', expect.any(AbortSignal));
    stream.stop();
    expect(stream.running).toBe(false);
  });

  it('gives up when signed out or without the Messages right', async () => {
    for (const status of [401, 403]) {
      const api: StreamOpener = { openStream: vi.fn(async () => ({ status, body: null })) };
      const s = new ChatStream(api, () => undefined, noSleep);
      s.start();
      await vi.waitFor(() => expect(s.running).toBe(false));
      expect(api.openStream).toHaveBeenCalledTimes(1);
    }
  });

  it('retries with backoff on network errors until stopped', async () => {
    const delays: number[] = [];
    let s!: ChatStream;
    const api: StreamOpener = { openStream: vi.fn(async () => { throw new TypeError('fetch failed'); }) };
    s = new ChatStream(api, () => undefined, async (ms) => {
      delays.push(ms);
      if (delays.length === 4) s.stop();
    });
    s.start();
    await vi.waitFor(() => expect(s.running).toBe(false));
    expect(delays).toEqual([1000, 2000, 4000, 8000]);
  });
});
