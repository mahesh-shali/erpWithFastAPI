import { describe, expect, it } from 'vitest';
import { ENVIRONMENTS, resolveEnvironment } from './environment';

describe('resolveEnvironment', () => {
  it('defaults to local in development and production in installed builds', () => {
    expect(resolveEnvironment(undefined, false)).toBe('local');
    expect(resolveEnvironment('', true)).toBe('production');
  });

  it('honours ERP_ENV over the default', () => {
    expect(resolveEnvironment('production', false)).toBe('production');
    expect(resolveEnvironment(' PROD ', false)).toBe('production');
    expect(resolveEnvironment('local', true)).toBe('local');
  });

  it('rejects unknown modes', () => {
    expect(() => resolveEnvironment('staging', false)).toThrow(/local.*production/);
  });
});

describe('ENVIRONMENTS', () => {
  it('points local at the Docker stack and production at the Render edge over https', () => {
    expect(ENVIRONMENTS.local.serverUrl).toBe('http://localhost:8088');
    expect(ENVIRONMENTS.production.serverUrl).toBe('https://erpwithfastapi.onrender.com');
  });
});
