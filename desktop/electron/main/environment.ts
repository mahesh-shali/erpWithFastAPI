import type { ErpEnvironment } from '../shared/contract';

/**
 * Where each mode connects. Both URLs are an edge that serves /api/* and /ai/* on one origin:
 * - local:      nginx of the Docker stack (`./local.sh up` in erpWithFastAPI)
 * - production: the Render static site, which rewrites /api/* and /ai/* to the API and AI services
 *               (render.yaml in https://github.com/mahesh-shali/erpWithFastAPI)
 */
export const ENVIRONMENTS: Record<ErpEnvironment, { label: string; serverUrl: string }> = {
  local: { label: 'Local (Docker)', serverUrl: 'http://localhost:8088' },
  production: { label: 'Production', serverUrl: 'https://erpwithfastapi.onrender.com' },
};

/**
 * ERP_ENV=local|production picks the mode. Without it, development runs (npm run dev) use local and
 * installed builds use production.
 */
export function resolveEnvironment(value: string | undefined, isPackaged: boolean): ErpEnvironment {
  const v = value?.trim().toLowerCase();
  if (!v) return isPackaged ? 'production' : 'local';
  if (v === 'local' || v === 'production') return v;
  if (v === 'prod') return 'production';
  throw new Error(`ERP_ENV must be "local" or "production" (got "${value}")`);
}
