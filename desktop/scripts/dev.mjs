// Development: Vite dev server (hot reload) + bundled main/preload + Electron pointed at Vite.
//   node scripts/dev.mjs          local mode   (Docker stack, http://localhost:8088)
//   node scripts/dev.mjs --prod   production mode (https://erpwithfastapi.onrender.com)
import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import { createServer } from 'vite';

const require = createRequire(import.meta.url);
const electronPath = require('electron');

const server = await createServer({ configFile: 'vite.config.ts' });
await server.listen();
const url = server.resolvedUrls.local[0];

await new Promise((resolve, reject) => {
  const b = spawn(process.execPath, ['scripts/build-electron.mjs'], { stdio: 'inherit' });
  b.on('exit', (code) => (code === 0 ? resolve() : reject(new Error('electron build failed'))));
});

const env = { ...process.env, ELECTRON_RENDERER_URL: url };
if (process.argv.includes('--prod')) env.ERP_ENV = 'production';
else env.ERP_ENV ??= 'local';
// VS Code terminals set this, which makes Electron run as plain Node (`app` is undefined).
delete env.ELECTRON_RUN_AS_NODE;
console.log(`Starting Electron in ${env.ERP_ENV} mode`);
const child = spawn(electronPath, ['.'], { stdio: 'inherit', env });
child.on('exit', async (code) => {
  await server.close();
  process.exit(code ?? 0);
});
