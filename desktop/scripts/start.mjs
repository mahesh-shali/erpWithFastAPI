// Runs the built app (npm run build first) with ERP_ENV set, the same on Windows, macOS and Linux.
//   node scripts/start.mjs          local mode
//   node scripts/start.mjs --prod   production mode
import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';

const electronPath = createRequire(import.meta.url)('electron');
const env = { ...process.env, ERP_ENV: process.argv.includes('--prod') ? 'production' : 'local' };
delete env.ELECTRON_RUN_AS_NODE;
spawn(electronPath, ['.'], { stdio: 'inherit', env }).on('exit', (code) => process.exit(code ?? 0));
