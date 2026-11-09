"""
Runs against a real Postgres built by the EF Core migrations + demo seed
(`dotnet run --project backend/src/Erp.Api -- --migrate --seed`; CI uses a service container).
Set TEST_AI_DATABASE_URL=postgresql://erp_ai_ro:ai_readonly_dev@localhost:5432/erp
"""
import os

import pytest

from app.db import PostgresExecutor, QueryError
from app.sql_guard import validate_sql
from tests.conftest import ORG_A, ORG_B

DSN = os.getenv("TEST_AI_DATABASE_URL")
pytestmark = [pytest.mark.integration,
              pytest.mark.skipif(not DSN, reason="TEST_AI_DATABASE_URL not set")]


@pytest.fixture
async def ex():
    e = await PostgresExecutor.create(DSN, 1, 2)
    yield e
    await e.close()


async def test_rls_isolates_tenants(ex):
    a = await ex.run(validate_sql("SELECT count(*) AS n FROM customers"), ORG_A)
    b = await ex.run(validate_sql("SELECT name FROM customers"), ORG_B)
    assert a.rows[0][0] == 200
    assert b.rows == [["SECRET OTHER TENANT CUSTOMER"]]


async def test_org_a_never_sees_org_b_rows_even_with_explicit_filter(ex):
    r = await ex.run(validate_sql(
        "SELECT count(*) FROM customers WHERE organization_id = '22222222-2222-2222-2222-222222222222'"), ORG_A)
    assert r.rows[0][0] == 0


async def test_role_cannot_write_even_if_guard_is_bypassed(ex):
    with pytest.raises(QueryError):
        await ex.run("DELETE FROM customers", ORG_A)


async def test_role_cannot_read_users_table(ex):
    with pytest.raises(QueryError):
        await ex.run("SELECT * FROM users", ORG_A)


async def test_audit_log_write_works_via_security_definer(ex):
    import uuid
    await ex.audit(ORG_A, uuid.uuid4(), "how many customers?", "SELECT 1", 1, 12)


async def test_role_still_cannot_insert_directly_in_rw_transaction(ex):
    async with ex._pool.acquire() as conn:
        async with conn.transaction():
            await conn.execute("SET TRANSACTION READ WRITE")
            with pytest.raises(Exception, match="permission denied"):
                await conn.execute("INSERT INTO customers(organization_id, name) "
                                   "VALUES ('11111111-1111-1111-1111-111111111111', 'x')")


async def test_no_org_set_returns_nothing(ex):
    # Bypass set_config by passing an unrelated org: RLS returns zero rows
    import uuid
    r = await ex.run(validate_sql("SELECT count(*) FROM orders"), uuid.uuid4())
    assert r.rows[0][0] == 0


async def test_session_check_function_works_for_ai_role(ex):
    import uuid
    assert await ex.session_active(uuid.uuid4(), uuid.uuid4()) is False


async def test_ai_role_cannot_read_sessions_table(ex):
    with pytest.raises(QueryError):
        await ex.run("SELECT refresh_token_hash FROM user_sessions", ORG_A)


async def test_org_users_view_is_tenant_scoped_and_has_no_secrets(ex):
    a = await ex.run(validate_sql("SELECT email, role FROM org_users"), ORG_A)
    assert ["admin@demo.com", "SuperAdmin"] in a.rows
    b = await ex.run(validate_sql("SELECT email FROM org_users"), ORG_B)
    assert ["admin@demo.com"] not in b.rows
    cols = await ex.run(validate_sql("SELECT * FROM org_users"), ORG_A)
    assert "password_hash" not in cols.columns



async def test_ai_user_access_reports_session_and_readable_pages(ex):
    import uuid
    active, pages = await ex.user_access(uuid.uuid4(), uuid.uuid4())
    assert active is False and pages == []
