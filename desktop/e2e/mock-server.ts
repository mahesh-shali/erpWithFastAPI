import { randomBytes, randomUUID } from 'node:crypto';
import http from 'node:http';
import type { AddressInfo } from 'node:net';

/**
 * A small stand-in for the ERP API, with the same auth semantics as backend/ (AuthController + SessionService):
 * 15-min access token, HttpOnly refresh cookie on /api/auth, rotation on every refresh, reuse = revoke.
 * Enough for the desktop e2e test to run without Docker/.NET.
 */
export interface MockServer { url: string; close(): Promise<void>; log: string[]; revokeAll(): void }

const user = { id: 'u1', email: 'admin@demo.com', fullName: 'Demo Admin', role: 'SuperAdmin', organizationId: 'o1' };
const menu = (moduleKey: string | null, module: string | null, group: string | null, sortOrder: number) => ({
  moduleKey, module, moduleIcon: moduleKey ? 'settings' : null, moduleSort: 1,
  groupKey: group?.toLowerCase() ?? null, group, groupSort: 1, sortOrder,
});
const page = (pageKey: string, name: string, route: string, m: ReturnType<typeof menu>) => ({
  pageKey, name, route, menu: m, isImplemented: true,
  isVisible: true, isRestricted: false, canRead: true, canWrite: true, canUpdate: true, canDelete: true,
});
const pages = [
  page('dashboard', 'Dashboard', '/', menu(null, null, null, 1)),
  page('analytics', 'Analytics', '/analytics', menu(null, null, null, 2)),
  page('customers', 'Customers', '/master/customers', menu('master', 'Master', 'Parties', 1)),
  page('products', 'Products', '/master/products', menu('master', 'Master', 'Items', 2)),
  page('master-users', 'Users', '/master/users', menu('master', 'Master', 'Users', 3)),
];

interface Session { id: string; refresh: string; previous: string | null; revoked: boolean; ua: string }

export async function startMockServer(): Promise<MockServer> {
  const sessions: Session[] = [];
  const access = new Map<string, Session>();
  const log: string[] = [];

  const issue = (res: http.ServerResponse, s: Session) => {
    const token = randomBytes(16).toString('hex');
    access.set(token, s);
    res.setHeader('Set-Cookie', `erp_refresh=${s.refresh}; path=/api/auth; samesite=strict; httponly`);
    send(res, 200, { accessToken: token, expiresAt: new Date(Date.now() + 9e5).toISOString(), user, sessionId: s.id,
      sessionExpiresAt: new Date(Date.now() + 7 * 864e5).toISOString() });
  };
  const send = (res: http.ServerResponse, status: number, body?: unknown) => {
    res.writeHead(status, { 'Content-Type': 'application/json' });
    res.end(body === undefined ? undefined : JSON.stringify(body));
  };
  const cookie = (req: http.IncomingMessage) => /erp_refresh=([^;]+)/.exec(req.headers.cookie ?? '')?.[1];

  const server = http.createServer(async (req, res) => {
    let raw = '';
    for await (const chunk of req) raw += chunk;
    const body = raw ? JSON.parse(raw) : undefined;
    const path = req.url ?? '';
    log.push(`${req.method} ${path}`);
    const bearer = /^Bearer (.+)$/.exec(req.headers.authorization ?? '')?.[1];
    const current = bearer ? access.get(bearer) : undefined;
    const authed = current && !current.revoked;

    if (req.method === 'POST' && path === '/api/auth/login') {
      if (body?.email !== 'admin@demo.com' || body?.password !== 'Admin@123') return send(res, 401, { title: 'Invalid email or password.' });
      const s = { id: randomUUID(), refresh: randomBytes(16).toString('hex'), previous: null, revoked: false, ua: String(req.headers['user-agent']) };
      sessions.push(s);
      return issue(res, s);
    }
    if (req.method === 'POST' && path === '/api/auth/refresh') {
      const c = cookie(req);
      const reused = sessions.find((x) => x.previous === c);
      if (reused) reused.revoked = true; // replay of a rotated token = theft
      const s = sessions.find((x) => x.refresh === c && !x.revoked);
      if (!s) return send(res, 401, { title: 'Session expired. Please sign in again.' });
      s.previous = s.refresh;
      s.refresh = randomBytes(16).toString('hex');
      return issue(res, s);
    }
    if (req.method === 'POST' && path === '/api/auth/logout') {
      const s = current ?? sessions.find((x) => x.refresh === cookie(req));
      if (s) s.revoked = true;
      return send(res, 204);
    }
    if (!authed) return send(res, 401, { title: 'Unauthorized' });

    if (path === '/api/me/permissions') return send(res, 200, { role: 'SuperAdmin', pages });
    if (path === '/api/dashboard/stats') return send(res, 200, { customers: 42, products: 17, orders: 128, revenue: 98765.5, lowStockProducts: 3 });
    if (path === '/api/auth/sessions') {
      return send(res, 200, sessions.filter((s) => !s.revoked).map((s) => ({
        id: s.id, createdAt: new Date().toISOString(), lastSeenAt: new Date().toISOString(),
        expiresAt: new Date(Date.now() + 864e5).toISOString(), ipAddress: '127.0.0.1', userAgent: s.ua, current: s === current,
      })));
    }
    if (path.startsWith('/api/analytics')) {
      const totals = { revenue: 98765.5, orders: 128, averageOrderValue: 771.6, newCustomers: 9 };
      const trend = Array.from({ length: 30 }, (_, i) => ({
        date: new Date(Date.now() - (29 - i) * 864e5).toISOString().slice(0, 10), revenue: 2000 + ((i * 937) % 3000), orders: 2 + (i % 6),
      }));
      const ranked = [{ name: 'Acme Ltd', revenue: 32000, count: 21 }, { name: 'Globex', revenue: 18000, count: 12 }];
      return send(res, 200, { days: 30, bucket: 'day', from: trend[0].date, to: trend[29].date, current: totals,
        previous: { ...totals, revenue: 81000, orders: 110 }, trend,
        statuses: [{ status: 'Paid', orders: 90, amount: 70000 }, { status: 'Pending', orders: 38, amount: 28765.5 }],
        topProducts: ranked, topCustomers: ranked, cities: ranked, categories: ranked });
    }
    if (path.startsWith('/api/customers')) {
      return send(res, 200, { items: [{ id: 'c1', name: 'Acme Ltd', email: 'ops@acme.test', city: 'Bengaluru', country: 'India' }], page: 1, pageSize: 25, total: 1 });
    }
    return send(res, 404, { title: `No mock for ${req.method} ${path}` });
  });

  await new Promise<void>((r) => server.listen(0, '127.0.0.1', r));
  const { port } = server.address() as AddressInfo;
  return {
    url: `http://127.0.0.1:${port}`,
    log,
    revokeAll: () => sessions.forEach((s) => { s.revoked = true; }),
    close: () => new Promise((r) => server.close(() => r())),
  };
}
