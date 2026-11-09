// Writes dist/_redirects for Netlify: proxy /api and /ai to the backends, SPA fallback for everything else.
// Netlify config files can't read environment variables in redirect rules, so they're generated here.
import { writeFileSync } from 'node:fs';

const clean = (name) => {
  const v = (process.env[name] ?? '').trim().replace(/\/+$/, '');
  if (!/^https:\/\/[^\s/]+$/.test(v)) {
    console.error(`${name} must be set to an https origin, e.g. https://example.com (got "${v}")`);
    process.exit(1);
  }
  return v;
};

const api = clean('API_ORIGIN');
const ai = clean('AI_ORIGIN');

// 200 = proxy (rewrite), ! = force even if a file exists. Order matters: first match wins.
const rules = [
  `/api/*  ${api}/api/:splat  200!`,
  `/health/*  ${api}/health/:splat  200!`,
  `/ai/*   ${ai}/ai/:splat   200!`,
  `/*      /index.html       200`,
].join('\n');

writeFileSync('dist/_redirects', rules + '\n');
console.log('dist/_redirects:\n' + rules);
