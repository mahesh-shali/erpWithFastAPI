import { describe, expect, it } from 'vitest';
import { SPLASH_PS1, splashArgs } from './update-splash';

const base = { version: '1.2.0', bounds: { x: -1280, y: 40, width: 1360.4, height: 860 }, scale: 1.5, readyFile: 'C:\tmp\r.ready' };

describe('splashArgs', () => {
  it('passes the window bounds, scale and version as plain arguments', () => {
    const args = splashArgs('C:\tmp\s.ps1', base);
    expect(args.slice(0, 8)).toEqual(['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', 'C:\tmp\s.ps1']);
    const named = Object.fromEntries(args.slice(8).reduce<string[][]>((acc, v, i) => (i % 2 ? acc[acc.length - 1].push(v) : acc.push([v]), acc), []));
    expect(named).toEqual({
      '-Version': '1.2.0', '-X': '-1280', '-Y': '40', '-W': '1360', '-H': '860', '-Scale': '1.50',
      '-ReadyFile': 'C:\tmp\r.ready', '-TimeoutSec': '180',
    });
  });

  it('drops a version that is not a plain version string, and clamps odd values', () => {
    const args = splashArgs('s.ps1', { ...base, version: '1.0"; Remove-Item C:\ -Recurse; "', scale: 99, bounds: { x: NaN, y: 0, width: 10, height: 10 } });
    expect(args[args.indexOf('-Version') + 1]).toBe('');
    expect(args[args.indexOf('-Scale') + 1]).toBe('1.00');
    expect(args[args.indexOf('-X') + 1]).toBe('0');
    expect(args[args.indexOf('-W') + 1]).toBe('400');
  });
});

describe('SPLASH_PS1', () => {
  it('closes when the new version is up or on timeout, and signals when shown', () => {
    expect(SPLASH_PS1).toMatch(/StartTime -gt \$started -and \$_\.MainWindowHandle -ne 0/);
    expect(SPLASH_PS1).toMatch(/TotalSeconds -gt \$TimeoutSec/);
    expect(SPLASH_PS1).toMatch(/Set-Content -LiteralPath \$ReadyFile/);
  });
});
