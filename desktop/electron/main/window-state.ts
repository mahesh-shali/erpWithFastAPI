import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';

export interface Rect { x: number; y: number; width: number; height: number }
export interface WindowState { bounds: Rect; maximized: boolean }

export const DEFAULT_SIZE = { width: 1360, height: 860 };
const MIN_VISIBLE = 120; // px of the title bar that must be on a screen to accept saved bounds

/**
 * Saved bounds are used only if enough of the window lands on a current display (a monitor may have been
 * unplugged since). Otherwise the default size, centred by Electron, is used.
 */
export function usableBounds(saved: Rect | undefined, workAreas: Rect[], min = { width: 960, height: 600 }): Partial<Rect> {
  if (!saved || ![saved.x, saved.y, saved.width, saved.height].every(Number.isFinite)) return { ...DEFAULT_SIZE };
  const width = Math.max(saved.width, min.width);
  const height = Math.max(saved.height, min.height);
  const visible = workAreas.some((a) => {
    const overlapX = Math.min(saved.x + width, a.x + a.width) - Math.max(saved.x, a.x);
    const titleOnScreen = saved.y >= a.y && saved.y < a.y + a.height;
    return overlapX >= MIN_VISIBLE && titleOnScreen;
  });
  return visible ? { x: saved.x, y: saved.y, width, height } : { width, height };
}

/** Window size, position and maximised state in userData/window-state.json. */
export class WindowStateStore {
  constructor(private readonly file: string) {}

  static in(userDataDir: string): WindowStateStore {
    return new WindowStateStore(path.join(userDataDir, 'window-state.json'));
  }

  /** Synchronous: needed before the window is created. */
  load(): WindowState | null {
    try {
      const s = JSON.parse(readFileSync(this.file, 'utf8')) as WindowState;
      return s && typeof s === 'object' && s.bounds ? { bounds: s.bounds, maximized: Boolean(s.maximized) } : null;
    } catch {
      return null;
    }
  }

  /** Synchronous: called from the window's 'close' event, which may be the last thing before the app quits. */
  save(state: WindowState): void {
    try {
      mkdirSync(path.dirname(this.file), { recursive: true });
      writeFileSync(this.file, JSON.stringify(state));
    } catch {
      /* losing the window position is harmless */
    }
  }
}
