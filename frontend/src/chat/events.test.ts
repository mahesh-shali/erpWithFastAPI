import { describe, expect, it } from 'vitest';
import { readSse } from './events';

function streamOf(chunks: string[]) {
  const enc = new TextEncoder();
  return new ReadableStream<Uint8Array>({
    start(c) {
      chunks.forEach((x) => c.enqueue(enc.encode(x)));
      c.close();
    },
  });
}

describe('readSse', () => {
  it('parses events split across chunks and ignores heartbeats', async () => {
    const got: [string, string][] = [];
    await readSse(streamOf([
      'retry: 3000\nevent: ready\ndata: {}\n\n: ping\n\n',
      'event: chat\ndata: {"type":"mess',
      'age","conversationId":"c1","messageId":7}\r\n\r\n',
    ]), (e, d) => got.push([e, d]));
    expect(got).toEqual([
      ['ready', '{}'],
      ['chat', '{"type":"message","conversationId":"c1","messageId":7}'],
    ]);
  });
});
