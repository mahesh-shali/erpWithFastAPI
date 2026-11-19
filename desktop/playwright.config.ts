import { defineConfig } from '@playwright/test';

// Launches the built Electron app (npm run build first; `npm run test:e2e` does both) against e2e/mock-server.ts.
export default defineConfig({
  testDir: 'e2e',
  timeout: 60_000,
  workers: 1,
  reporter: [['list']],
  use: { trace: 'off' },
});
