// k6 load test for the ERP API.
//   k6 run -e BASE_URL=http://localhost -e PROFILE=smoke  load-tests/k6/api.js
//   k6 run -e BASE_URL=https://staging   -e PROFILE=stress load-tests/k6/api.js
// For million-user scale, run distributed (k6 Cloud / k6-operator on k8s) against staging.
import http from 'k6/http';
import { check, sleep } from 'k6';

const BASE = __ENV.BASE_URL || 'http://localhost';

const PROFILES = {
  smoke:  { vus: 20, duration: '30s' },
  load:   { stages: [{ duration: '2m', target: 500 }, { duration: '5m', target: 500 }, { duration: '1m', target: 0 }] },
  stress: { stages: [{ duration: '5m', target: 5000 }, { duration: '10m', target: 20000 }, { duration: '3m', target: 0 }] },
  spike:  { stages: [{ duration: '30s', target: 100 }, { duration: '30s', target: 10000 }, { duration: '1m', target: 100 }] },
};

export const options = {
  ...PROFILES[__ENV.PROFILE || 'smoke'],
  thresholds: {
    http_req_failed: ['rate<0.01'],                 // <1% errors (429s count - tune limits for tests)
    'http_req_duration{endpoint:stats}': ['p(95)<300'],
    'http_req_duration{endpoint:products}': ['p(95)<300'],
  },
};

export function setup() {
  const res = http.post(`${BASE}/api/auth/login`,
    JSON.stringify({ email: 'admin@demo.com', password: 'Admin@123' }),
    { headers: { 'Content-Type': 'application/json' } });
  check(res, { 'login ok': (r) => r.status === 200 });
  return { token: res.json('accessToken') };
}

export default function ({ token }) {
  const params = (endpoint) => ({ headers: { Authorization: `Bearer ${token}` }, tags: { endpoint } });

  const stats = http.get(`${BASE}/api/dashboard/stats`, params('stats'));
  check(stats, { 'stats 200': (r) => r.status === 200 });

  const page = 1 + Math.floor(Math.random() * 5);
  const products = http.get(`${BASE}/api/products?page=${page}&pageSize=20`, params('products'));
  check(products, { 'products 200': (r) => r.status === 200 });

  sleep(1);
}
