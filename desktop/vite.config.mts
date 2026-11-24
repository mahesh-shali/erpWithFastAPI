/// <reference types="vitest" />
import { defineConfig, type Plugin } from 'vite';
import react from '@vitejs/plugin-react';

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
    rollupOptions: {
      output: { manualChunks: { vendor: ['react', 'react-dom', 'react-router-dom'] } },
    },
  },
  test: {
    globals: true,
    setupFiles: './src/test/setup.ts',
    include: ['src/**/*.test.{ts,tsx}', 'electron/**/*.test.ts'],
    environmentMatchGlobs: [['src/**', 'jsdom'], ['electron/**', 'node']],
  },
});
