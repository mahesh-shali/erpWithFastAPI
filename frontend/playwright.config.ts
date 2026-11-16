import { defineConfig, devices } from '@playwright/test';

// E2E runs against the full docker-compose stack (nginx on :80).
export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  retries: process.env.CI ? 2 : 0,
  // `npm run test:e2e:report` opens the HTML report (every test, steps, screenshots and traces on failure).
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://localhost',
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
