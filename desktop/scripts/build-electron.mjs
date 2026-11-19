// Bundles the Electron main process and preload script into dist-electron/ (CommonJS).
// The preload must be a single file: sandboxed preloads cannot require() local modules.
import { build } from 'esbuild';

const watch = process.argv.includes('--watch');
const common = {
  bundle: true,
  platform: 'node',
  format: 'cjs',
  target: 'node20',
  external: ['electron'],
  sourcemap: true,
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
  await Promise.all(builds.map((b) => build(b)));
}
