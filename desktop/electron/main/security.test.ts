import { describe, expect, it, vi } from 'vitest';

vi.mock('electron', () => ({}));
const { isAppUrl } = await import('./security');

describe('isAppUrl', () => {
  const dir = '/opt/ERP Platform/resources/app.asar/dist';
  it('accepts only the bundled UI in production', () => {
    expect(isAppUrl('file:///opt/ERP%20Platform/resources/app.asar/dist/index.html#/login', undefined, dir)).toBe(true);
    expect(isAppUrl('file:///etc/passwd', undefined, dir)).toBe(false);
    expect(isAppUrl('https://evil.test/', undefined, dir)).toBe(false);
  });
  it('accepts only the Vite origin in development', () => {
    expect(isAppUrl('http://localhost:5174/#/', 'http://localhost:5174/', dir)).toBe(true);
    expect(isAppUrl('http://localhost:5175/', 'http://localhost:5174/', dir)).toBe(false);
  });
});
