import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { DEFAULT_SIZE, usableBounds, WindowStateStore } from './window-state';

const primary = { x: 0, y: 0, width: 1920, height: 1040 };
const right = { x: 1920, y: 0, width: 1920, height: 1040 };

describe('usableBounds', () => {
  it('reuses saved bounds that are on a current display', () => {
    expect(usableBounds({ x: 100, y: 50, width: 1200, height: 800 }, [primary])).toEqual({ x: 100, y: 50, width: 1200, height: 800 });
    expect(usableBounds({ x: 2000, y: 10, width: 1300, height: 900 }, [primary, right])).toEqual({ x: 2000, y: 10, width: 1300, height: 900 });
  });

  it('keeps the size but lets the window centre when its monitor is gone', () => {
    expect(usableBounds({ x: 2000, y: 10, width: 1300, height: 900 }, [primary])).toEqual({ width: 1300, height: 900 });
    expect(usableBounds({ x: 100, y: -500, width: 1300, height: 900 }, [primary])).toEqual({ width: 1300, height: 900 });
  });

  it('uses the default size without saved bounds and never goes below the minimum', () => {
    expect(usableBounds(undefined, [primary])).toEqual(DEFAULT_SIZE);
    expect(usableBounds({ x: 10, y: 10, width: 200, height: 100 }, [primary])).toEqual({ x: 10, y: 10, width: 960, height: 600 });
  });
});

describe('WindowStateStore', () => {
  let dir: string;
  afterEach(() => rmSync(dir, { recursive: true, force: true }));

  it('saves and loads the state, and returns null when nothing usable is stored', () => {
    dir = mkdtempSync(path.join(tmpdir(), 'erp-ws-'));
    const store = WindowStateStore.in(dir);
    expect(store.load()).toBeNull();
    store.save({ bounds: { x: 1, y: 2, width: 1000, height: 700 }, maximized: true });
    expect(store.load()).toEqual({ bounds: { x: 1, y: 2, width: 1000, height: 700 }, maximized: true });
  });
});
