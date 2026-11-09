-- =============================================================
-- Roles
--   erp_app      : used by ASP.NET Core API (read/write, owner-level, bypasses RLS
--                  because the API enforces org scoping in EF Core query filters)
--   erp_ai_ro    : used by the FastAPI AI service. SELECT-only, subject to RLS,
--                  cannot see users / password hashes / audit log.
-- Passwords here are dev defaults - override via secrets in production.
-- =============================================================
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'erp_ai_ro') THEN
    CREATE ROLE erp_ai_ro LOGIN PASSWORD 'ai_readonly_dev' NOSUPERUSER NOCREATEDB NOCREATEROLE;
  END IF;
END $$;

REVOKE ALL ON ALL TABLES IN SCHEMA public FROM erp_ai_ro;
GRANT USAGE ON SCHEMA public TO erp_ai_ro;
GRANT SELECT ON customers, products, orders, order_items TO erp_ai_ro;

-- Defence in depth: hard limits on the AI role
ALTER ROLE erp_ai_ro SET statement_timeout = '5s';
ALTER ROLE erp_ai_ro SET default_transaction_read_only = on;
ALTER ROLE erp_ai_ro SET idle_in_transaction_session_timeout = '10s';
ALTER ROLE erp_ai_ro CONNECTION LIMIT 200;

-- Chat audit is written by the AI service through a SECURITY DEFINER function,
-- so the role itself never gets INSERT rights on any table.
CREATE OR REPLACE FUNCTION log_chat(p_org UUID, p_user UUID, p_q TEXT, p_sql TEXT, p_rows INT, p_ms INT)
RETURNS VOID LANGUAGE sql SECURITY DEFINER SET search_path = public AS $$
  INSERT INTO chat_audit_log(organization_id, user_id, question, generated_sql, row_count, latency_ms)
  VALUES (p_org, p_user, p_q, p_sql, p_rows, p_ms);
$$;
REVOKE ALL ON FUNCTION log_chat FROM PUBLIC;
GRANT EXECUTE ON FUNCTION log_chat TO erp_ai_ro;
