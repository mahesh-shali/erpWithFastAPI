-- =============================================================
-- ERP Platform schema (multi-tenant, Row-Level Security enabled)
-- =============================================================
CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE TABLE organizations (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name        TEXT NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE users (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    email           TEXT NOT NULL UNIQUE,
    password_hash   TEXT NOT NULL,
    full_name       TEXT NOT NULL,
    role            TEXT NOT NULL DEFAULT 'User' CHECK (role IN ('Admin','User')),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE customers (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    name            TEXT NOT NULL,
    email           TEXT,
    city            TEXT,
    country         TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE products (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    sku             TEXT NOT NULL,
    name            TEXT NOT NULL,
    category        TEXT,
    unit_price      NUMERIC(12,2) NOT NULL CHECK (unit_price >= 0),
    stock_quantity  INT NOT NULL DEFAULT 0 CHECK (stock_quantity >= 0),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (organization_id, sku)
);

CREATE TABLE orders (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    customer_id     UUID NOT NULL REFERENCES customers(id),
    status          TEXT NOT NULL DEFAULT 'Pending'
                    CHECK (status IN ('Pending','Paid','Shipped','Cancelled')),
    total_amount    NUMERIC(14,2) NOT NULL DEFAULT 0,
    ordered_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE order_items (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    order_id    UUID NOT NULL REFERENCES orders(id) ON DELETE CASCADE,
    organization_id UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    product_id  UUID NOT NULL REFERENCES products(id),
    quantity    INT NOT NULL CHECK (quantity > 0),
    unit_price  NUMERIC(12,2) NOT NULL
);

CREATE TABLE chat_audit_log (
    id              BIGSERIAL PRIMARY KEY,
    organization_id UUID NOT NULL,
    user_id         UUID NOT NULL,
    question        TEXT NOT NULL,
    generated_sql   TEXT,
    row_count       INT,
    latency_ms      INT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- ---------- Indexes for hot paths ----------
CREATE INDEX ix_users_org            ON users(organization_id);
CREATE INDEX ix_customers_org        ON customers(organization_id);
CREATE INDEX ix_products_org_cat     ON products(organization_id, category);
CREATE INDEX ix_orders_org_date      ON orders(organization_id, ordered_at DESC);
CREATE INDEX ix_orders_customer      ON orders(customer_id);
CREATE INDEX ix_order_items_order    ON order_items(order_id);
CREATE INDEX ix_order_items_org_prod ON order_items(organization_id, product_id);
CREATE INDEX ix_chat_audit_org_date  ON chat_audit_log(organization_id, created_at DESC);

-- ---------- Row-Level Security (tenant isolation) ----------
-- Every business table is filtered by current_setting('app.org_id').
-- The AI read-only role is subject to RLS, so even a hallucinated query
-- can never read another tenant's rows.
DO $$
DECLARE t TEXT;
BEGIN
  FOREACH t IN ARRAY ARRAY['customers','products','orders','order_items'] LOOP
    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %I USING (organization_id = NULLIF(current_setting(''app.org_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;
