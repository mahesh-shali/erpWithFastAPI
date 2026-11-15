// Realistic load test across every ERP API, with profiles from smoke to breakpoint.
//
//   ./local.sh load smoke          (runs in the k6 container; metrics stream to Grafana)
//   ./local.sh load stress
//   k6 run -e BASE_URL=https://staging.example.com -e PROFILE=load load-tests/k6/erp-all-apis.js
//
// Env:
//   PROFILE      smoke | load | stress | spike | soak | breakpoint     (default smoke)
//   VUS, DURATION   override the VU count / hold time of the chosen profile
//   USERS        size of the logged-in user pool created in setup()     (default 50)
//   THINK        seconds a user pauses between actions, randomised ±50% (default 1)
//   AI=1         also run the /ai/chat scenario (costs Groq tokens for uncached questions)
//   ADMIN_EMAIL / ADMIN_PASSWORD   (default admin@demo.com / Admin@123, the demo seed)
//
// What one virtual user does: the mix of a real ERP session — mostly reads (dashboard, lists, details),
// some writes (orders, customers), occasional admin screens. A separate low-rate scenario runs the full
// sign-in flow (login → me → refresh → logout), which is the most CPU-expensive path (bcrypt).
import http from 'k6/http';
import { check, group, sleep, fail } from 'k6';
import { Counter } from 'k6/metrics';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.1.0/index.js';

const BASE = __ENV.BASE_URL || 'http://localhost:8088';
const PROFILE = __ENV.PROFILE || 'smoke';
const USERS = Number(__ENV.USERS || 50);
const THINK = Number(__ENV.THINK ?? 1);
const ADMIN = { email: __ENV.ADMIN_EMAIL || 'admin@demo.com', password: __ENV.ADMIN_PASSWORD || 'Admin@123' };
const LT_PASSWORD = 'LoadTest@12345';
const LT_PRODUCTS = 50;

const businessErrors = new Counter('erp_business_errors');

// ---------------- profiles ----------------
const vus = (n) => Number(__ENV.VUS || n);
const hold = (d) => __ENV.DURATION || d;
const PROFILES = {
  smoke:  { browse: { executor: 'constant-vus', vus: vus(5), duration: hold('1m') }, authRate: 1 },
  load:   { browse: { executor: 'ramping-vus', stages: [
              { duration: '2m', target: vus(200) }, { duration: hold('5m'), target: vus(200) }, { duration: '1m', target: 0 }] },
            authRate: 5 },
  stress: { browse: { executor: 'ramping-vus', stages: [
              { duration: '2m', target: vus(500) }, { duration: '3m', target: vus(500) },
              { duration: '2m', target: vus(1000) }, { duration: '3m', target: vus(1000) },
              { duration: '2m', target: vus(1500) }, { duration: hold('3m'), target: vus(1500) },
              { duration: '2m', target: 0 }] },
            authRate: 10 },
  spike:  { browse: { executor: 'ramping-vus', stages: [
              { duration: '1m', target: 50 }, { duration: '20s', target: vus(1500) },
              { duration: hold('1m'), target: vus(1500) }, { duration: '20s', target: 50 }, { duration: '1m', target: 50 }] },
            authRate: 10 },
  soak:   { browse: { executor: 'constant-vus', vus: vus(300), duration: hold('30m') }, authRate: 3 },
  // Open model: keeps raising iterations/s regardless of latency until thresholds abort the run.
  // The rate at abort time is this deployment's capacity.
  breakpoint: { browse: { executor: 'ramping-arrival-rate', startRate: 10, timeUnit: '1s',
                preAllocatedVUs: 200, maxVUs: vus(3000),
                stages: [{ duration: hold('15m'), target: 3000 }] },
                authRate: 5, abort: true },
};
const P = PROFILES[PROFILE] || fail(`Unknown PROFILE ${PROFILE}`);
const abort = !!P.abort;

const scenarios = {
  browse: { ...P.browse, exec: 'browse', ...(P.browse.executor === 'ramping-vus' ? { gracefulRampDown: '30s' } : {}) },
  auth_flow: { executor: 'constant-arrival-rate', exec: 'authFlow', rate: P.authRate, timeUnit: '1s',
               duration: totalDuration(P.browse), preAllocatedVUs: 5, maxVUs: 50 },
};
if (__ENV.AI === '1') {
  scenarios.ai_chat = { executor: 'constant-vus', exec: 'aiChat', vus: Number(__ENV.AI_VUS || 5),
                        duration: totalDuration(P.browse) };
}

export const options = {
  scenarios,
  setupTimeout: '5m',
  discardResponseBodies: false,
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
  thresholds: {
    http_req_failed: [{ threshold: 'rate<0.01', abortOnFail: abort, delayAbortEval: '30s' }],
    'http_req_duration{type:read}': [{ threshold: 'p(95)<500', abortOnFail: abort, delayAbortEval: '30s' }],
    'http_req_duration{type:write}': ['p(95)<1000'],
    'http_req_duration{type:auth}': ['p(95)<1500'],
    checks: ['rate>0.99'],
  },
};

// ---------------- helpers ----------------
const json = { 'Content-Type': 'application/json' };
const pick = (a) => a[Math.floor(Math.random() * a.length)];
const chance = (p) => Math.random() < p;
const think = (scale = 1) => THINK > 0 && sleep(THINK * scale * (0.5 + Math.random()));

function req(method, path, token, { body, name, type = 'read', expect = [200] } = {}) {
  const params = {
    headers: token ? { ...json, Authorization: `Bearer ${token}` } : json,
    tags: { name: name || `${method} ${path}`, type },
    responseCallback: http.expectedStatuses(...expect),
  };
  const res = http.request(method, `${BASE}${path}`, body === undefined ? null : JSON.stringify(body), params);
  const ok = check(res, { [`${params.tags.name} -> ${expect.join('/')}`]: (r) => expect.includes(r.status) });
  if (!ok && res.status >= 400 && res.status < 500 && res.status !== 429) businessErrors.add(1, { name: params.tags.name });
  return res;
}

function login(email, password) {
  const res = req('POST', '/api/auth/login', null, { body: { email, password }, name: 'POST /api/auth/login', type: 'auth' });
  return res.status === 200 ? res.json('accessToken') : null;
}

function totalDuration(s) {
  const secs = (d) => { const m = /^(\d+)(ms|s|m|h)$/.exec(d); return m ? Number(m[1]) * { ms: 0.001, s: 1, m: 60, h: 3600 }[m[2]] : 0; };
  const t = s.stages ? s.stages.reduce((a, st) => a + secs(st.duration), 0) : secs(s.duration);
  return `${Math.ceil(t)}s`;
}

// ---------------- setup: user pool, test products, ids ----------------
export function setup() {
  const health = http.get(`${BASE}/health/ready`);
  if (health.status !== 200) fail(`API not ready at ${BASE} (/health/ready -> ${health.status})`);

  const admin = login(ADMIN.email, ADMIN.password) || fail(`Admin login failed for ${ADMIN.email}`);

  // Role for pool users: the highest one the admin may assign (so they can use every business screen).
  const roles = req('GET', '/api/roles/assignable', admin, { name: 'GET /api/roles/assignable' }).json();
  const role = roles.sort((a, b) => b.level - a.level)[0].name;

  // Pool of real users, each with its own session (exercises per-user caches and rate-limit partitions).
  const tokens = [];
  for (let i = 1; i <= USERS; i++) {
    const email = `loadtest.user${i}@demo.com`;
    req('POST', '/api/users', admin, { body: { email, fullName: `Load Test ${i}`, password: LT_PASSWORD, role },
        name: 'POST /api/users', type: 'write', expect: [201, 409] });
    const t = login(email, LT_PASSWORD);
    if (t) tokens.push(t);
  }
  if (!tokens.length) fail('No load-test user could log in');

  // Dedicated products with huge stock, so orders never fail on stock and seed data stays intact.
  const existing = req('GET', '/api/products?category=LoadTest&pageSize=100', admin,
                       { name: 'GET /api/products' }).json('items');
  const bySku = Object.fromEntries(existing.map((p) => [p.sku, p]));
  const productIds = [];
  for (let i = 1; i <= LT_PRODUCTS; i++) {
    const sku = `LT-${String(i).padStart(4, '0')}`;
    const body = { sku, name: `Load test product ${i}`, category: 'LoadTest', unitPrice: 10 + i, stockQuantity: 1_000_000_000 };
    if (bySku[sku]) {
      req('PUT', `/api/products/${bySku[sku].id}`, admin, { body, name: 'PUT /api/products/{id}', type: 'write' });
      productIds.push(bySku[sku].id);
    } else {
      const r = req('POST', '/api/products', admin, { body, name: 'POST /api/products', type: 'write', expect: [201] });
      if (r.status === 201) productIds.push(r.json('id'));
    }
  }

  const customerIds = req('GET', '/api/customers?pageSize=100', admin, { name: 'GET /api/customers' })
    .json('items').map((c) => c.id);
  const orders = req('GET', '/api/orders?limit=100', admin, { name: 'GET /api/orders' }).json();
  const userList = req('GET', '/api/users', admin, { name: 'GET /api/users' }).json();
  const ltUserIds = userList.filter((u) => u.email.startsWith('loadtest.user')).map((u) => u.id);

  // Separate accounts for the login/logout scenario so it never ends a browsing user's session.
  const authUsers = [];
  for (let i = 1; i <= 10; i++) {
    const email = `loadtest.auth${i}@demo.com`;
    req('POST', '/api/users', admin, { body: { email, fullName: `Load Auth ${i}`, password: LT_PASSWORD, role },
        name: 'POST /api/users', type: 'write', expect: [201, 409] });
    authUsers.push(email);
  }

  console.log(`setup: ${tokens.length} users (${role}), ${productIds.length} products, ` +
              `${customerIds.length} customers, profile=${PROFILE}`);
  return { admin, tokens, productIds, customerIds, orderIds: orders.map((o) => o.id), ltUserIds, authUsers };
}

// ---------------- scenario: a user working in the ERP ----------------
export function browse(d) {
  const token = d.tokens[(__VU - 1) % d.tokens.length];
  const r = Math.random();

  if (r < 0.10) group('open app', () => {
    req('GET', '/api/auth/me', token);
    req('GET', '/api/me/permissions', token);
    req('GET', '/api/dashboard/stats', token);
  });
  else if (r < 0.25) group('dashboard', () => req('GET', '/api/dashboard/stats', token));
  else if (r < 0.42) group('products', () => {
    const cat = chance(0.3) ? `&category=${pick(['Electronics', 'Furniture', 'Stationery', 'Apparel'])}` : '';
    req('GET', `/api/products?page=${1 + Math.floor(Math.random() * 5)}&pageSize=20${cat}`, token,
        { name: 'GET /api/products' });
  });
  else if (r < 0.60) group('customers', () => {
    const search = chance(0.3) ? `&search=${pick(['a', 'e', 'Cust', '1', 'Ltd'])}` : '';
    req('GET', `/api/customers?page=${1 + Math.floor(Math.random() * 10)}&pageSize=20${search}`, token,
        { name: 'GET /api/customers' });
    req('GET', `/api/customers/${pick(d.customerIds)}`, token, { name: 'GET /api/customers/{id}' });
  });
  else if (r < 0.76) group('orders', () => {
    const status = chance(0.2) ? `&status=${pick(['Pending', 'Paid', 'Shipped'])}` : '';
    const page1 = req('GET', `/api/orders?limit=20${status}`, token, { name: 'GET /api/orders' });
    const list = page1.status === 200 ? page1.json() : [];
    if (list.length === 20 && chance(0.4)) {   // next page via keyset cursor
      req('GET', `/api/orders?limit=20&before=${encodeURIComponent(list[19].orderedAt)}${status}`, token,
          { name: 'GET /api/orders' });
    }
    if (d.orderIds.length) req('GET', `/api/orders/${pick(d.orderIds)}`, token, { name: 'GET /api/orders/{id}' });
  });
  else if (r < 0.88) group('create order', () => {
    const items = [...d.productIds].sort(() => Math.random() - 0.5).slice(0, 1 + Math.floor(Math.random() * 3))
      .map((productId) => ({ productId, quantity: 1 + Math.floor(Math.random() * 5) }));
    const o = req('POST', '/api/orders', token, { body: { customerId: pick(d.customerIds), items },
                  name: 'POST /api/orders', type: 'write', expect: [201] });
    if (o.status === 201 && chance(0.5)) {
      req('PATCH', `/api/orders/${o.json('id')}/status`, token, { body: { status: 'Paid' },
          name: 'PATCH /api/orders/{id}/status', type: 'write' });
    }
  });
  else if (r < 0.93) group('customer CRUD', () => {
    const c = req('POST', '/api/customers', token, {
      body: { name: `LT Customer ${__VU}-${__ITER}`, email: `lt${__VU}x${__ITER}@example.com`, city: 'Pune', country: 'India' },
      name: 'POST /api/customers', type: 'write', expect: [201] });
    if (c.status === 201) {
      req('GET', `/api/customers/${c.json('id')}`, token, { name: 'GET /api/customers/{id}' });
      req('DELETE', `/api/customers/${c.json('id')}`, token, { name: 'DELETE /api/customers/{id}', type: 'write', expect: [204] });
    }
  });
  else if (r < 0.97) group('admin screens', () => {
    req('GET', '/api/users', d.admin);
    req('GET', '/api/roles', d.admin);
    req('GET', '/api/master/pages', d.admin);
    req('GET', `/api/master/roles/${pick([1, 2])}/rights`, d.admin, { name: 'GET /api/master/roles/{id}/rights' });
    if (d.ltUserIds.length) {
      req('GET', `/api/master/users/${pick(d.ltUserIds)}/rights`, d.admin, { name: 'GET /api/master/users/{id}/rights' });
    }
  });
  else group('sessions', () => req('GET', '/api/auth/sessions', token));

  think();
}

// ---------------- scenario: full sign-in flow ----------------
export function authFlow(d) {
  const token = login(pick(d.authUsers), LT_PASSWORD);
  if (!token) return;
  req('GET', '/api/auth/me', token);
  // The refresh cookie (Path=/api/auth) is in this VU's cookie jar from the login response.
  req('POST', '/api/auth/refresh', null, { name: 'POST /api/auth/refresh', type: 'auth' });
  req('POST', '/api/auth/logout', token, { name: 'POST /api/auth/logout', type: 'auth', expect: [204] });
}

// ---------------- scenario: AI assistant (opt-in) ----------------
const QUESTIONS = ['What is my total revenue?', 'How many customers do I have?',
                   'Top 5 products by quantity sold', 'Which city has the most customers?'];
export function aiChat(d) {
  const token = d.tokens[(__VU - 1) % d.tokens.length];
  req('POST', '/ai/chat', token, { body: { question: pick(QUESTIONS), history: [] },
      name: 'POST /ai/chat', type: 'ai', expect: [200, 429] });
  think(5);
}

// ---------------- report ----------------
export function handleSummary(data) {
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const out = { stdout: textSummary(data, { indent: ' ', enableColors: true }) };
  if (__ENV.RESULTS_DIR) out[`${__ENV.RESULTS_DIR}/${PROFILE}-${stamp}.json`] = JSON.stringify(data, null, 2);
  return out;
}
