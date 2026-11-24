import { describe, expect, it, vi } from 'vitest';

vi.mock('electron', () => ({}));
const { forbiddenSwitch, isAppUrl } = await import('./security');

describe('forbiddenSwitch', () => {
  it('finds debugging and security-bypass switches in any spelling', () => {
    expect(forbiddenSwitch(['app.exe', '--remote-debugging-port=9222'])).toBe('remote-debugging-port');
    expect(forbiddenSwitch(['app.exe', '--REMOTE-DEBUGGING-PIPE'])).toBe('remote-debugging-pipe');
    expect(forbiddenSwitch(['app.exe', '--inspect'])).toBe('inspect');
    expect(forbiddenSwitch(['app.exe', '-inspect-brk=0'])).toBe('inspect-brk');
    expect(forbiddenSwitch(['app.exe', '--ignore-certificate-errors'])).toBe('ignore-certificate-errors');
    expect(forbiddenSwitch(['app.exe', '--disable-web-security'])).toBe('disable-web-security');
    expect(forbiddenSwitch(['app.exe', '--no-sandbox'])).toBe('no-sandbox');
    expect(forbiddenSwitch(['app.exe', '--js-flags=--allow-natives-syntax'])).toBe('js-flags');
  });
  it('allows ordinary launches', () => {
    expect(forbiddenSwitch(['C:\\Program Files\\ERP Platform\\ERP Platform.exe'])).toBeNull();
    expect(forbiddenSwitch(['app.exe', '--erp-app-version=1.3.0', '--updated', 'C:\\inspect\\file.txt'])).toBeNull();
    // Only exact switch names count: a longer name that starts the same way is not a match
    expect(forbiddenSwitch(['app.exe', '--inspectors=1'])).toBeNull();
  });
});

describe('isAppUrl', () => {
  const dir = '/opt/ERP Platform/resources/app.asar/dist';
  it('accepts only the bundled UI in production', () => {
    expect(isAppUrl('file:///opt/ERP%20Platform/resources/app.asar/dist/index.html#/login', undefined, dir)).toBe(true);
    expect(isAppUrl('file:///etc/passwd', undefined, dir)).toBe(false);
    expect(isAppUrl('https://evil.test/', undefined, dir)).toBe(false);
    // A sibling folder that merely starts with the same name is not the app
    expect(isAppUrl('file:///opt/ERP%20Platform/resources/app.asar/dist-evil/index.html', undefined, dir)).toBe(false);
  });
  it('accepts only the Vite origin in development', () => {
    expect(isAppUrl('http://localhost:5174/#/', 'http://localhost:5174/', dir)).toBe(true);
    expect(isAppUrl('http://localhost:5175/', 'http://localhost:5174/', dir)).toBe(false);
  });
});
