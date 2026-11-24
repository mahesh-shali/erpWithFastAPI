/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

/** React and the router (plus their runtime deps) change rarely, so they get their own long-cached chunk. */
const VENDOR = /[\\/]node_modules[\\/](react|react-dom|react-router|react-router-dom|scheduler|cookie|set-cookie-parser)[\\/]/;

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:8080',
      '/ai': 'http://localhost:8000',
    },
  },
  build: {
    rolldownOptions: {
      output: { codeSplitting: { groups: [{ name: 'vendor', test: VENDOR }] } },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: './src/test/setup.ts',
    include: ['src/**/*.test.{ts,tsx}'],
    coverage: { reporter: ['text', 'lcov'], include: ['src/**/*.{ts,tsx}'], exclude: ['src/test/**', 'src/main.tsx'] },
  },
});
