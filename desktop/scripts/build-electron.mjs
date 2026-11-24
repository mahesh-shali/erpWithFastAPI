// Bundles the Electron main process and preload script into dist-electron/ (CommonJS).
// The preload must be a single file: sandboxed preloads cannot require() local modules.
// Release builds are minified and carry no source maps, so the installed app doesn't ship readable source;
// `--watch` (npm run dev) keeps source maps for debugging.
import { rmSync } from 'node:fs';
import { build } from 'esbuild';

const watch = process.argv.includes('--watch');
const common = {
  bundle: true,
  platform: 'node',
  format: 'cjs',
  target: 'node20',
  external: ['electron'],
  sourcemap: watch,
  minify: !watch,
  legalComments: watch ? 'inline' : 'none',
  drop: watch ? [] : ['debugger'],
  logLevel: 'info',
};

const builds = [
  { ...common, entryPoints: ['electron/main/index.ts'], outfile: 'dist-electron/main.cjs' },
  { ...common, entryPoints: ['electron/preload/index.ts'], outfile: 'dist-electron/preload.cjs' },
];

if (watch) {
  const { context } = await import('esbuild');
  for (const b of builds) await (await context(b)).watch();
} else {
  rmSync('dist-electron', { recursive: true, force: true }); // no stale .map files from an earlier dev build
  await Promise.all(builds.map((b) => build(b)));
}
