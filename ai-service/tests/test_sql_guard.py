import pytest

from app.sql_guard import SqlRejected, validate_sql


@pytest.mark.parametrize("sql", [
    "SELECT count(*) FROM customers",
    "select name, unit_price from products where category = 'Electronics' order by unit_price desc limit 5",
    "SELECT c.city, sum(o.total_amount) FROM orders o JOIN customers c ON c.id = o.customer_id GROUP BY c.city",
    "WITH monthly AS (SELECT date_trunc('month', ordered_at) m, sum(total_amount) t FROM orders GROUP BY 1) "
    "SELECT * FROM monthly ORDER BY m",
    "SELECT name FROM products UNION SELECT name FROM customers",
    "SELECT * FROM public.orders;",
    "SELECT p.name, sum(oi.quantity) FROM order_items oi JOIN products p ON p.id = oi.product_id GROUP BY p.name",
])
def test_valid_selects_pass_and_get_wrapped_with_limit(sql):
    safe = validate_sql(sql, max_rows=500)
    assert safe.startswith("SELECT * FROM (")
    assert safe.endswith("LIMIT 500")


@pytest.mark.parametrize("sql,reason", [
    ("DELETE FROM orders", "Only SELECT"),
    ("UPDATE products SET unit_price = 0", "Only SELECT"),
    ("INSERT INTO customers(name) VALUES ('x')", "Only SELECT"),
    ("DROP TABLE orders", "Only SELECT"),
    ("TRUNCATE orders", ""),
    ("SELECT 1; DROP TABLE orders", "Exactly one statement"),
    ("SELECT * FROM users", "Table not allowed"),
    ("SELECT password_hash FROM users", "Table not allowed"),
    ("SELECT * FROM chat_audit_log", "Table not allowed"),
    ("SELECT * FROM pg_catalog.pg_user", "Schema not allowed"),
    ("SELECT * FROM information_schema.tables", "Schema not allowed"),
    ("SELECT pg_sleep(10)", "Function not allowed"),
    ("SELECT pg_read_file('/etc/passwd')", "Function not allowed"),
    ("SELECT current_setting('app.org_id')", "Function not allowed"),
    ("SELECT set_config('app.org_id', 'x', false)", "Function not allowed"),
    ("SELECT * FROM generate_series(1, 100000000)", ""),
    ("SELECT * INTO stolen FROM customers", "Forbidden operation"),
    ("WITH d AS (DELETE FROM orders RETURNING *) SELECT * FROM d", "Forbidden operation"),
    ("", "Empty"),
    ("this is not sql", ""),
])
def test_dangerous_or_invalid_sql_is_rejected(sql, reason):
    with pytest.raises(SqlRejected) as e:
        validate_sql(sql)
    assert reason in str(e.value)


def test_cte_name_shadowing_forbidden_table_is_still_checked_inside():
    # CTE named like an allowed alias but reading a forbidden table
    with pytest.raises(SqlRejected):
        validate_sql("WITH x AS (SELECT * FROM users) SELECT * FROM x")


def test_comments_are_stripped_from_output():
    safe = validate_sql("SELECT count(*) /* sneaky */ FROM customers -- trailing")
    assert "sneaky" not in safe and "trailing" not in safe


def test_max_rows_is_integer_only():
    assert validate_sql("SELECT 1 FROM orders", max_rows=10).endswith("LIMIT 10")


def test_org_users_view_allowed_but_users_table_still_blocked():
    assert "org_users" in validate_sql("SELECT role, count(*) FROM org_users GROUP BY role")
    with pytest.raises(SqlRejected, match="Table not allowed"):
        validate_sql("SELECT u.email FROM org_users o JOIN users u ON u.id = o.id")


def test_allowed_tables_narrow_per_user():
    from app.sql_guard import tables_for_pages
    allowed = tables_for_pages({"orders", "ai-chat"})
    assert allowed == {"orders", "order_items"}
    validate_sql("SELECT count(*) FROM orders o JOIN order_items i ON i.order_id = o.id", allowed_tables=allowed)
    with pytest.raises(SqlRejected, match="no permission"):
        validate_sql("SELECT c.city FROM orders o JOIN customers c ON c.id = o.customer_id", allowed_tables=allowed)


@pytest.mark.parametrize("fn", ["log_chat", "session_is_active", "ai_user_access", "effective_page_permissions"])
def test_internal_security_functions_cannot_be_called(fn):
    with pytest.raises(SqlRejected):
        validate_sql(f"SELECT {fn}('00000000-0000-0000-0000-000000000000', '00000000-0000-0000-0000-000000000000')")
