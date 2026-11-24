/// <reference types="vitest/config" />
import { defineConfig, type Plugin } from 'vite';
import react from '@vitejs/plugin-react';

/** React and the router (plus their runtime deps) change rarely, so they get their own chunk. */
const VENDOR = /[\\/]node_modules[\\/](react|react-dom|react-router|react-router-dom|scheduler|cookie|set-cookie-parser)[\\/]/;

/**
 * Production Content-Security-Policy. The renderer talks to nobody: all HTTP goes through the main
 * process (window.erp), so connect-src is 'none'. Not applied in dev, where Vite needs inline HMR scripts
 * and a websocket.
 */
const CSP = [
  "default-src 'none'",
  "script-src 'self'",
  "style-src 'self' 'unsafe-inline'",
  "img-src 'self' data:",
  "font-src 'self'",
  "connect-src 'none'",
  "base-uri 'none'",
  "form-action 'none'",
  "frame-ancestors 'none'",
].join('; ');

const csp = (): Plugin => ({
  name: 'erp-csp',
  apply: 'build',
  transformIndexHtml: () => [{ tag: 'meta', attrs: { 'http-equiv': 'Content-Security-Policy', content: CSP }, injectTo: 'head-prepend' }],
});

export default defineConfig({
  base: './', // loaded from file:// in the packaged app
  plugins: [react(), csp()],
  // Don't watch build output: electron-builder's temp files in release/ crash the watcher (EBUSY) during `npm run dist`.
  server: { port: 5174, strictPort: true, watch: { ignored: ['**/release/**', '**/dist-electron/**'] } },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    rolldownOptions: {
      output: { codeSplitting: { groups: [{ name: 'vendor', test: VENDOR }] } },
    },
  },
  test: {
    globals: true,
    setupFiles: './src/test/setup.ts',
    // The renderer runs in a DOM; the Electron main/preload code runs in Node.
    projects: [
      { extends: true, test: { name: 'renderer', include: ['src/**/*.test.{ts,tsx}'], environment: 'jsdom' } },
      { extends: true, test: { name: 'electron', include: ['electron/**/*.test.ts'], environment: 'node' } },
    ],
  },
});
