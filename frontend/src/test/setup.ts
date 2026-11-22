import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterEach, vi } from 'vitest';
import { resetLastPageForTests } from '../components/lastPage';

afterEach(() => {
  cleanup();
  globalThis.localStorage?.clear(); // absent in "@vitest-environment node" tests
  resetLastPageForTests();
  vi.restoreAllMocks();
});
