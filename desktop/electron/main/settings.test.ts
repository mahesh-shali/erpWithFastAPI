import { describe, expect, it, vi } from 'vitest';

vi.mock('electron', () => ({ app: { getPath: () => '/tmp/erp-desktop-test' } }));
const { normalizeServerUrl } = await import('./settings');

describe('normalizeServerUrl', () => {
  it('accepts https and trims trailing slashes, query and hash', () => {
    expect(normalizeServerUrl(' https://erp.example.com/ ')).toBe('https://erp.example.com');
    expect(normalizeServerUrl('https://erp.example.com/base/?x=1#y')).toBe('https://erp.example.com/base');
  });

  it('allows plain http only for loopback', () => {
    expect(normalizeServerUrl('http://localhost:8088')).toBe('http://localhost:8088');
    expect(normalizeServerUrl('http://127.0.0.1:8080')).toBe('http://127.0.0.1:8080');
    expect(() => normalizeServerUrl('http://erp.example.com', false)).toThrow(/https/);
    expect(normalizeServerUrl('http://10.0.0.5', true)).toBe('http://10.0.0.5');
  });

  it('rejects other schemes, embedded credentials and garbage', () => {
    expect(() => normalizeServerUrl('file:///etc/passwd')).toThrow();
    expect(() => normalizeServerUrl('https://user:pw@erp.example.com')).toThrow(/credentials/);
    expect(() => normalizeServerUrl('not a url')).toThrow(/full URL/);
  });
});
