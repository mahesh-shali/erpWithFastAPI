import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterEach, vi } from 'vitest';
import { fakeBridge, resetFakeBridge } from './fakeBridge';

if (typeof window !== 'undefined') window.erp = fakeBridge;

afterEach(() => {
  cleanup();
  resetFakeBridge();
  vi.restoreAllMocks();
});
