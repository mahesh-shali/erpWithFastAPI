#!/usr/bin/env node
/**
 * One visual report of every test case in the repo.
 *
 *   node scripts/test-report.mjs                 run the default suites, write test-reports/index.html
 *   node scripts/test-report.mjs --open          ...and open it in the browser
 *   node scripts/test-report.mjs --only frontend,ai
 *   node scripts/test-report.mjs --e2e           also run Playwright (needs the app running; E2E_BASE_URL, default http://localhost)
 *   node scripts/test-report.mjs --no-run        rebuild the HTML from the last raw results without re-running
 *
 * Suites: frontend (Vitest) · backend-unit (xUnit) · backend-integration (xUnit + Testcontainers, needs Docker)
 *         ai (pytest, mocked LLM) · ai-integration (pytest vs real Postgres, needs TEST_AI_DATABASE_URL) · e2e (Playwright)
 * Each test row shows its result, duration and the exact command to re-run just that test.
 * Manual checks per screen come from scripts/manual-checks.json and can be ticked off in the page.
 */
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const OUT = join(ROOT, 'test-reports');
const RAW = join(OUT, 'raw');
mkdirSync(RAW, { recursive: true });

const args = process.argv.slice(2);
const flag = (f) => args.includes(f);
const onlyArg = args.find((a, i) => args[i - 1] === '--only');
const E2E_URL = process.env.E2E_BASE_URL ?? 'http://localhost';

const ok = (cmd) => spawnSync(cmd, { shell: true, stdio: 'ignore' }).status === 0;
const hasDocker = () => ok('docker info');
const PY = ['ai-service/.venv/Scripts/python.exe', 'ai-service/.venv/bin/python']
  .map((p) => join(ROOT, p)).find(existsSync) ?? 'python';
const q = (s) => `"${s}"`;

// ---------------------------------------------------------------- suite definitions
const AI_AREAS = {
  test_agent: 'LLM agent loop', test_api: 'Chat API (auth, cache, rate limit, rights)', test_config: 'Settings & production checks',
  test_sql_guard: 'SQL guard', test_db_integration: 'Database security (RLS, read-only role)',
};
const SUITES = [
  {
    id: 'frontend', layer: 'Frontend (React)', kind: 'Unit / screen', default: true,
    run: `npx vitest run --reporter=json --outputFile=${q(join(RAW, 'frontend.json'))}`, cwd: 'frontend',
    rerunAll: 'cd frontend && npm test', parse: parseVitest,
  },
  {
    id: 'backend-unit', layer: 'Backend (ASP.NET Core)', kind: 'Unit', default: true,
    run: `dotnet test backend/tests/Erp.Api.UnitTests --logger ${q('trx;LogFileName=' + join(RAW, 'backend-unit.trx'))}`,
    rerunAll: 'dotnet test backend/tests/Erp.Api.UnitTests',
    parse: () => parseTrx('backend-unit.trx', 'backend/tests/Erp.Api.UnitTests'),
  },
  {
    id: 'backend-integration', layer: 'Backend (ASP.NET Core)', kind: 'API integration (real Postgres + Redis)',
    default: true, needs: () => hasDocker() || 'Docker is not running (Testcontainers needs it)',
    run: `dotnet test backend/tests/Erp.Api.IntegrationTests --logger ${q('trx;LogFileName=' + join(RAW, 'backend-integration.trx'))}`,
    rerunAll: 'dotnet test backend/tests/Erp.Api.IntegrationTests',
    parse: () => parseTrx('backend-integration.trx', 'backend/tests/Erp.Api.IntegrationTests'),
  },
  {
    id: 'ai', layer: 'AI service (FastAPI)', kind: 'Unit (mocked LLM)', default: true,
    run: `${q(PY)} -m pytest -m "not integration" -q --junitxml=${q(join(RAW, 'ai.xml'))}`, cwd: 'ai-service',
    rerunAll: 'cd ai-service && python -m pytest -m "not integration"', parse: () => parseJunit('ai.xml'),
  },
  {
    id: 'ai-integration', layer: 'AI service (FastAPI)', kind: 'DB security integration (real Postgres)',
    default: true, needs: () => !!process.env.TEST_AI_DATABASE_URL || 'TEST_AI_DATABASE_URL is not set',
    run: `${q(PY)} -m pytest -m integration -q --junitxml=${q(join(RAW, 'ai-integration.xml'))}`, cwd: 'ai-service',
    rerunAll: 'cd ai-service && TEST_AI_DATABASE_URL=postgresql://erp_ai_ro:<password>@localhost:5432/erp python -m pytest -m integration',
    parse: () => parseJunit('ai-integration.xml'),
  },
  {
    id: 'e2e', layer: 'End-to-end (browser)', kind: 'Playwright against the running app', default: false,
    run: 'npx playwright test --reporter=json', cwd: 'frontend', stdoutTo: 'e2e.json',
    env: { E2E_BASE_URL: E2E_URL },
    rerunAll: `cd frontend && E2E_BASE_URL=${E2E_URL} npx playwright test`, parse: parsePlaywright,
  },
];

const wanted = onlyArg ? onlyArg.split(',') : SUITES.filter((s) => s.default || (s.id === 'e2e' && flag('--e2e'))).map((s) => s.id);

// ---------------------------------------------------------------- run
const results = [];
for (const s of SUITES) {
  if (!wanted.includes(s.id)) { results.push({ ...s, status: 'not-run', note: 'not selected', tests: [] }); continue; }
  const blocker = s.needs?.();
  if (blocker && blocker !== true && !flag('--no-run')) {
    console.log(`- ${s.id.padEnd(20)} skipped: ${blocker}`);
    results.push({ ...s, status: 'not-run', note: blocker, tests: [] });
    continue;
  }
  let seconds = null;
  if (!flag('--no-run')) {
    process.stdout.write(`▶ ${s.id.padEnd(20)} running… `);
    const t0 = Date.now();
    const r = spawnSync(s.run, {
      shell: true, cwd: join(ROOT, s.cwd ?? '.'), encoding: 'utf8', maxBuffer: 256 * 1024 * 1024,
      env: { ...process.env, ...s.env, FORCE_COLOR: '0', CI: process.env.CI ?? '' },
    });
    seconds = (Date.now() - t0) / 1000;
    writeFileSync(join(RAW, `${s.id}.log`), `${r.stdout ?? ''}\n${r.stderr ?? ''}`);
    if (s.stdoutTo) writeFileSync(join(RAW, s.stdoutTo), r.stdout ?? '');
  }
  let tests = [];
  let note;
  try { tests = s.parse(); } catch (e) { note = `could not read results (${e.message}); see test-reports/raw/${s.id}.log`; }
  const failed = tests.filter((t) => t.status === 'failed').length;
  const status = note ? 'error' : failed ? 'failed' : 'passed';
  if (!flag('--no-run')) console.log(`${tests.length} tests, ${failed} failed (${seconds?.toFixed(0)}s)${note ? ' — ' + note : ''}`);
  results.push({ ...s, status, note, seconds, tests });
}

writeFileSync(join(OUT, 'index.html'), html(results));
const all = results.flatMap((r) => r.tests);
const failedCount = all.filter((t) => t.status === 'failed').length;
console.log(`\nReport: ${join(OUT, 'index.html')}  (${all.length} tests, ${failedCount} failed)`);
if (flag('--open')) spawnSync(process.platform === 'win32' ? `start "" ${q(join(OUT, 'index.html'))}` :
  `${process.platform === 'darwin' ? 'open' : 'xdg-open'} ${q(join(OUT, 'index.html'))}`, { shell: true });
process.exitCode = failedCount || results.some((r) => r.status === 'error') ? 1 : 0;

// ---------------------------------------------------------------- parsers
function rx(s) { return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&').replace(/"/g, '.'); }
function human(s) { return s.replace(/Tests$/, '').replace(/([a-z])([A-Z])/g, '$1 $2').replace(/_/g, ' ').trim(); }

function parseVitest() {
  const data = JSON.parse(readFileSync(join(RAW, 'frontend.json'), 'utf8'));
  return data.testResults.flatMap((file) => {
    const rel = relative(join(ROOT, 'frontend'), file.name).replaceAll('\\', '/');
    return file.assertionResults.map((a) => ({
      area: a.ancestorTitles[0] ?? rel, name: [...a.ancestorTitles.slice(1), a.title].join(' › '), file: `frontend/${rel}`,
      status: a.status === 'passed' ? 'passed' : a.status === 'failed' ? 'failed' : 'skipped',
      ms: Math.round(a.duration ?? 0), error: (a.failureMessages ?? []).join('\n'),
      cmd: `cd frontend && npx vitest run ${rel} -t "${rx([...a.ancestorTitles, a.title].join(' '))}"`,
    }));
  });
}

function attrs(tag) {
  const o = {};
  for (const m of tag.matchAll(/(\w+)="([^"]*)"/g)) o[m[1]] = decode(m[2]);
  return o;
}
function decode(s) {
  return s.replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&lt;/g, '<').replace(/&gt;/g, '>')
    .replace(/&#x([0-9a-f]+);/gi, (_, h) => String.fromCodePoint(parseInt(h, 16))).replace(/&amp;/g, '&');
}

function parseTrx(file, project) {
  const xml = readFileSync(join(RAW, file), 'utf8');
  const out = [];
  for (const m of xml.matchAll(/<UnitTestResult\b([^>]*?)(\/>|>([\s\S]*?)<\/UnitTestResult>)/g)) {
    const a = attrs(m[1]);
    const full = a.testName;                                   // Namespace.Class.Method(args)
    const base = full.replace(/\(.*\)$/s, '');
    const cls = base.split('.').slice(-2, -1)[0];
    const method = base.split('.').pop();
    const params = full.slice(base.length);
    const [h, mi, sec] = (a.duration ?? '0:0:0').split(':');
    const message = /<Message>([\s\S]*?)<\/Message>/.exec(m[3] ?? '')?.[1];
    out.push({
      area: human(cls), name: human(method) + (params ? ` ${params}` : ''), file: `${project}/${cls}.cs`,
      status: a.outcome === 'Passed' ? 'passed' : a.outcome === 'Failed' ? 'failed' : 'skipped',
      ms: Math.round(((+h * 60 + +mi) * 60 + +sec) * 1000), error: message ? decode(message) : '',
      cmd: `dotnet test ${project} --filter "FullyQualifiedName~${base}"`,
    });
  }
  return out;
}

function parseJunit(file) {
  const xml = readFileSync(join(RAW, file), 'utf8');
  const out = [];
  for (const m of xml.matchAll(/<testcase\b([^>]*?)(\/>|>([\s\S]*?)<\/testcase>)/g)) {
    const a = attrs(m[1]);
    const mod = a.classname.split('.').pop();
    const body = m[3] ?? '';
    const failed = /<(failure|error)\b/.test(body);
    out.push({
      area: AI_AREAS[mod] ?? mod, name: a.name.replace(/^test_/, '').replace(/_/g, ' '), file: `ai-service/tests/${mod}.py`,
      status: failed ? 'failed' : /<skipped\b/.test(body) ? 'skipped' : 'passed',
      ms: Math.round(parseFloat(a.time ?? '0') * 1000),
      error: failed ? decode(/<(?:failure|error)\b[^>]*>([\s\S]*?)<\/(?:failure|error)>/.exec(body)?.[1] ?? /message="([^"]*)"/.exec(body)?.[1] ?? '') : '',
      cmd: `cd ai-service && python -m pytest "tests/${mod}.py::${a.name}"`,
    });
  }
  return out;
}

function parsePlaywright() {
  const data = JSON.parse(readFileSync(join(RAW, 'e2e.json'), 'utf8'));
  const out = [];
  const walk = (suite, titles) => {
    for (const spec of suite.specs ?? []) for (const t of spec.tests) {
      const last = t.results.at(-1) ?? {};
      out.push({
        area: titles[0] ?? spec.file, name: [...titles.slice(1), spec.title].join(' › '), file: `frontend/e2e/${spec.file}`,
        status: t.status === 'skipped' ? 'skipped' : t.status === 'expected' || t.status === 'flaky' ? 'passed' : 'failed',
        ms: Math.round(last.duration ?? 0), error: last.error?.message?.replace(/\u001b\[\d+m/g, '') ?? '',
        cmd: `cd frontend && E2E_BASE_URL=${E2E_URL} npx playwright test e2e/${spec.file}:${spec.line}`,
      });
    }
    for (const s of suite.suites ?? []) walk(s, s.file && !titles.length && s.title === s.file ? titles : [...titles, s.title]);
  };
  for (const s of data.suites) walk(s, []);
  return out;
}

// ---------------------------------------------------------------- HTML
function html(results) {
  const manual = existsSync(join(ROOT, 'scripts/manual-checks.json'))
    ? JSON.parse(readFileSync(join(ROOT, 'scripts/manual-checks.json'), 'utf8')) : [];
  const data = { generated: new Date().toISOString(), suites: results.map(({ parse, needs, ...r }) => r), manual };
  const json = JSON.stringify(data).replace(/</g, '\\u003c');
  return `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>ERP Test Report</title>
<style>
:root{--bg:#f6f7f9;--card:#fff;--text:#1d2330;--muted:#6b7280;--border:#e5e7eb;--accent:#2f5bea;--accent-weak:#e8eefe;
--pass:#006300;--pass-bg:#e7f5e7;--fail:#c62828;--fail-bg:#fdecea;--skip:#7a5a00;--skip-bg:#fff4e5;color-scheme:light}
@media (prefers-color-scheme:dark){:root{--bg:#0f1115;--card:#181b21;--text:#e8eaee;--muted:#9aa3b2;--border:#2a2f38;--accent:#7b9bff;
--accent-weak:#1e2740;--pass:#5fd35f;--pass-bg:#15301a;--fail:#ff7b7b;--fail-bg:#3a1a1a;--skip:#f0c060;--skip-bg:#3a3018;color-scheme:dark}}
*{box-sizing:border-box}body{margin:0;font:14px/1.45 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;background:var(--bg);color:var(--text)}
main{max-width:1200px;margin:0 auto;padding:24px 16px 64px;display:grid;gap:16px}
h1{margin:0;font-size:22px}h2{margin:0 0 8px;font-size:16px}.muted{color:var(--muted)}
.card{background:var(--card);border:1px solid var(--border);border-radius:12px;padding:16px}
.summary{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:12px}
.tile b{display:block;font-size:26px;font-weight:600}.tile span{color:var(--muted);font-size:13px}
.suites{display:grid;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));gap:12px}
.suite h3{margin:0 0 2px;font-size:14px}.suite .kind{font-size:12px;color:var(--muted)}
.bar{display:flex;gap:2px;height:8px;margin:10px 0 6px}.bar i{border-radius:4px}
.bar .p{background:var(--pass)}.bar .f{background:var(--fail)}.bar .s{background:var(--skip)}
code,.cmd{font:12px/1.4 ui-monospace,SFMono-Regular,Consolas,monospace}
.cmd{display:flex;gap:6px;align-items:flex-start;background:var(--bg);border:1px solid var(--border);border-radius:6px;padding:4px 6px;word-break:break-all}
.cmd code{flex:1}button{font:inherit;cursor:pointer;border:1px solid var(--border);background:var(--card);color:var(--text);border-radius:6px;padding:2px 8px;font-size:12px}
button:hover{border-color:var(--accent)}
.tabs{display:flex;gap:4px;flex-wrap:wrap}.tabs button{padding:6px 14px;font-size:14px;border-radius:8px}
.tabs button[aria-selected=true]{background:var(--accent-weak);color:var(--accent);border-color:var(--accent);font-weight:600}
.filters{display:flex;gap:8px;flex-wrap:wrap;align-items:center}
.filters input,.filters select{font:inherit;padding:6px 10px;border:1px solid var(--border);border-radius:8px;background:var(--card);color:var(--text)}
.filters input{flex:1;min-width:200px}
details.area{border:1px solid var(--border);border-radius:10px;background:var(--card)}
details.area>summary{cursor:pointer;padding:10px 14px;display:flex;gap:10px;align-items:center;list-style:none}
details.area>summary::-webkit-details-marker{display:none}
details.area>summary .count{margin-left:auto;font-size:12px;color:var(--muted)}
.layer{font-size:11px;border-radius:999px;padding:1px 8px;background:var(--bg);border:1px solid var(--border);color:var(--muted);white-space:nowrap}
table{border-collapse:collapse;width:100%}td{border-top:1px solid var(--border);padding:8px 14px;vertical-align:top}
td.st{width:84px}td.ms{width:70px;text-align:right;color:var(--muted);font-variant-numeric:tabular-nums}
.pill{display:inline-block;border-radius:999px;padding:1px 8px;font-size:12px;font-weight:600}
.pill.passed{background:var(--pass-bg);color:var(--pass)}.pill.failed{background:var(--fail-bg);color:var(--fail)}
.pill.skipped,.pill.not-run{background:var(--skip-bg);color:var(--skip)}.pill.error{background:var(--fail-bg);color:var(--fail)}
.name{font-weight:500}.file{font-size:12px;color:var(--muted)}pre.err{white-space:pre-wrap;background:var(--fail-bg);color:var(--fail);padding:8px;border-radius:6px;font-size:12px;max-height:240px;overflow:auto}
.manual li{margin:4px 0}.check{display:flex;gap:10px;align-items:flex-start;border-top:1px solid var(--border);padding:12px 14px}
.check input{margin-top:3px;width:18px;height:18px}.check.done .ctitle{text-decoration:line-through;color:var(--muted)}
.ctitle{font-weight:600}.check ol{margin:6px 0;padding-left:20px}.expect{margin:4px 0 0}.expect b{color:var(--pass)}
[hidden]{display:none!important}@media (max-width:600px){td{padding:8px}td.ms{display:none}}
</style></head><body><main>
<header><h1>ERP Platform — Test report</h1><p class="muted" id="gen"></p></header>
<section class="summary" id="summary"></section>
<section class="card"><h2>Suites</h2><div class="suites" id="suites"></div></section>
<nav class="tabs" role="tablist"><button role="tab" aria-selected="true" data-tab="auto">Automated tests</button>
<button role="tab" aria-selected="false" data-tab="manual">Manual checks</button><button role="tab" aria-selected="false" data-tab="howto">How to run</button></nav>
<section id="tab-auto" class="grid">
<div class="filters card"><input id="q" type="search" placeholder="Search test, screen or file…" aria-label="Search">
<select id="layer" aria-label="Layer"><option value="">All layers</option></select>
<select id="status" aria-label="Status"><option value="">All results</option><option>passed</option><option>failed</option><option>skipped</option></select>
<button id="expand">Expand all</button></div>
<div id="areas" style="display:grid;gap:8px;margin-top:12px"></div></section>
<section id="tab-manual" hidden><div class="card"><h2>Manual verification, screen by screen</h2>
<p class="muted">Start the app (<code>./dev.sh</code>, then open http://localhost:5173). Tick each check as you verify it; ticks are kept in this browser.
<button id="resetChecks">Clear ticks</button></p><div id="manual"></div></div></section>
<section id="tab-howto" hidden><div class="card"><h2>Run a whole suite</h2><div id="howto" style="display:grid;gap:10px"></div>
<h2 style="margin-top:18px">Visual runners</h2><ul class="manual">
<li><b>Frontend, live in the browser:</b> <code>cd frontend && npm run test:ui</code> (Vitest UI with coverage)</li>
<li><b>End-to-end, step by step:</b> <code>cd frontend && E2E_BASE_URL=http://localhost:5173 npm run test:e2e:ui</code> (Playwright UI: watch each step, time-travel through the DOM)</li>
<li><b>Last end-to-end run:</b> <code>cd frontend && npm run test:e2e:report</code> (HTML report with traces of failures)</li>
<li><b>Coverage:</b> <code>cd frontend && npm run test:coverage</code> · <code>cd ai-service && python -m pytest -m "not integration" --cov=app --cov-report=html</code></li>
<li><b>This page again:</b> <code>node scripts/test-report.mjs --open</code> (add <code>--e2e</code> with the app running)</li></ul></div></section>
</main>
<script>
const DATA=${json};
const $=s=>document.querySelector(s);const esc=s=>String(s??'').replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
const tests=DATA.suites.flatMap(s=>s.tests.map(t=>({...t,layer:s.layer+' · '+s.kind,suite:s.id})));
const count=st=>tests.filter(t=>t.status===st).length;
$('#gen').textContent='Generated '+new Date(DATA.generated).toLocaleString()+' · '+DATA.suites.filter(s=>s.status!=='not-run').length+' of '+DATA.suites.length+' suites run';
const tile=(v,l)=>'<div class="card tile"><b>'+v+'</b><span>'+l+'</span></div>';
$('#summary').innerHTML=tile(tests.length,'test cases')+tile(count('passed'),'passed')+tile(count('failed'),'failed')+tile(count('skipped'),'skipped')+tile(DATA.manual.reduce((n,m)=>n+m.checks.length,0),'manual checks');
const copyBtn=c=>'<button data-copy="'+esc(c)+'" title="Copy command">Copy</button>';
$('#suites').innerHTML=DATA.suites.map(s=>{const p=s.tests.filter(t=>t.status==='passed').length,f=s.tests.filter(t=>t.status==='failed').length,k=s.tests.filter(t=>t.status==='skipped').length,n=s.tests.length||1;
return '<div class="suite"><h3>'+esc(s.layer)+' <span class="pill '+s.status+'">'+(s.status==='not-run'?'not run':s.status)+'</span></h3><div class="kind">'+esc(s.kind)+'</div>'+
(s.tests.length?'<div class="bar"><i class="p" style="flex:'+p/n+'"></i><i class="f" style="flex:'+f/n+'"></i><i class="s" style="flex:'+k/n+'"></i></div><div class="muted">'+p+' passed · '+f+' failed · '+k+' skipped'+(s.seconds?' · '+Math.round(s.seconds)+' s':'')+'</div>':'<p class="muted">'+esc(s.note||'')+'</p>')+
'<div class="cmd" style="margin-top:8px"><code>'+esc(s.rerunAll)+'</code>'+copyBtn(s.rerunAll)+'</div></div>'}).join('');
$('#howto').innerHTML=DATA.suites.map(s=>'<div><b>'+esc(s.layer)+'</b> — '+esc(s.kind)+'<div class="cmd"><code>'+esc(s.rerunAll)+'</code>'+copyBtn(s.rerunAll)+'</div></div>').join('');
for(const l of [...new Set(tests.map(t=>t.layer))]) $('#layer').insertAdjacentHTML('beforeend','<option>'+esc(l)+'</option>');
function render(){const q=$('#q').value.toLowerCase(),L=$('#layer').value,S=$('#status').value;
const shown=tests.filter(t=>(!L||t.layer===L)&&(!S||t.status===S)&&(!q||(t.area+' '+t.name+' '+t.file).toLowerCase().includes(q)));
const groups=new Map();for(const t of shown){const k=t.layer+'||'+t.area;if(!groups.has(k))groups.set(k,[]);groups.get(k).push(t)}
$('#areas').innerHTML=[...groups].map(([k,ts])=>{const [layer,area]=k.split('||');const f=ts.filter(t=>t.status==='failed').length;
return '<details class="area"'+(f||q?' open':'')+'><summary><span class="pill '+(f?'failed':'passed')+'">'+(f?f+' failed':'✓')+'</span><b>'+esc(area)+'</b><span class="layer">'+esc(layer)+'</span><span class="count">'+ts.length+' tests</span></summary><table>'+
ts.map(t=>'<tr><td class="st"><span class="pill '+t.status+'">'+t.status+'</span></td><td><div class="name">'+esc(t.name)+'</div><div class="file">'+esc(t.file)+'</div>'+
(t.error?'<pre class="err">'+esc(t.error.slice(0,4000))+'</pre>':'')+'<div class="cmd"><code>'+esc(t.cmd)+'</code>'+copyBtn(t.cmd)+'</div></td><td class="ms">'+t.ms+' ms</td></tr>').join('')+'</table></details>'}).join('')||'<p class="muted card">No tests match.</p>'}
['#q','#layer','#status'].forEach(s=>$(s).addEventListener('input',render));render();
$('#expand').onclick=()=>{const open=$('#expand').textContent==='Expand all';document.querySelectorAll('details.area').forEach(d=>d.open=open);$('#expand').textContent=open?'Collapse all':'Expand all'};
document.addEventListener('click',e=>{const c=e.target.closest('[data-copy]');if(!c)return;navigator.clipboard?.writeText(c.dataset.copy);c.textContent='Copied';setTimeout(()=>c.textContent='Copy',1200)});
document.querySelectorAll('[role=tab]').forEach(b=>b.onclick=()=>{document.querySelectorAll('[role=tab]').forEach(x=>x.setAttribute('aria-selected',x===b));
['auto','manual','howto'].forEach(t=>$('#tab-'+t).hidden=t!==b.dataset.tab)});
const KEY='erp-manual-checks';let ticks={};try{ticks=JSON.parse(localStorage.getItem(KEY)||'{}')}catch{}
function renderManual(){$('#manual').innerHTML=DATA.manual.map(m=>'<details class="area" open><summary><b>'+esc(m.screen)+'</b><span class="layer">'+esc(m.login)+'</span><span class="count">'+m.checks.filter((c,i)=>ticks[m.screen+i]).length+' / '+m.checks.length+' done</span></summary>'+
(m.url?'<div style="padding:0 14px 8px" class="muted">Open <code>'+esc(m.url)+'</code></div>':'')+
m.checks.map((c,i)=>'<label class="check'+(ticks[m.screen+i]?' done':'')+'"><input type="checkbox" data-k="'+esc(m.screen+i)+'"'+(ticks[m.screen+i]?' checked':'')+'><div><div class="ctitle">'+esc(c.title)+'</div><ol>'+c.steps.map(s=>'<li>'+esc(s)+'</li>').join('')+'</ol><p class="expect"><b>Expected:</b> '+esc(c.expect)+'</p>'+(c.auto?'<div class="file">Automated by: '+esc(c.auto)+'</div>':'')+'</div></label>').join('')+'</details>').join('')}
renderManual();
$('#manual').addEventListener('change',e=>{const k=e.target.dataset.k;if(!k)return;ticks[k]=e.target.checked;try{localStorage.setItem(KEY,JSON.stringify(ticks))}catch{}renderManual()});
$('#resetChecks').onclick=()=>{ticks={};try{localStorage.removeItem(KEY)}catch{}renderManual()};
</script></body></html>`;
}
