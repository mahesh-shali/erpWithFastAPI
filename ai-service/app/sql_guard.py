"""
Static validation of LLM-generated SQL.

This is layer 2 of 3. The real security boundary is the database itself:
  1. Postgres role `erp_ai_ro`: SELECT-only on 4 tables + the org_users view, read-only transactions,
     statement_timeout, and Row-Level Security pinned to the caller's org.
  2. This guard: rejects anything that isn't a single, plain SELECT over allowed tables.
  3. Hard LIMIT wrapper so a query can never stream millions of rows.
"""
import sqlglot
from sqlglot import exp

# org_users is a tenant-filtered view of login users without password hashes; the users table stays blocked.
ALLOWED_TABLES = frozenset({"customers", "products", "orders", "order_items", "org_users"})

# Page rights (Master module) -> tables the AI may query for that user. A user who can't read the
# Customers page can't ask the AI about customers either.
PAGE_TABLES: dict[str, frozenset[str]] = {
    "customers": frozenset({"customers"}),
    "products": frozenset({"products"}),
    "orders": frozenset({"orders", "order_items"}),
    "master-users": frozenset({"org_users"}),
}


def tables_for_pages(readable_pages: set[str] | frozenset[str]) -> frozenset[str]:
    return frozenset().union(*(PAGE_TABLES.get(p, frozenset()) for p in readable_pages)) & ALLOWED_TABLES

FORBIDDEN_NODES: tuple[type[exp.Expression], ...] = (
    exp.Insert, exp.Update, exp.Delete, exp.Drop, exp.Create, exp.Alter,
    exp.Merge, exp.Command, exp.Into, exp.Lock, exp.Set, exp.TruncateTable,
    exp.Grant, exp.Copy, exp.Transaction, exp.Commit, exp.Rollback,
)

FORBIDDEN_FUNCTIONS = frozenset({
    "pg_sleep", "pg_read_file", "pg_read_binary_file", "pg_ls_dir", "pg_stat_file",
    "lo_import", "lo_export", "lo_get", "dblink", "dblink_exec", "set_config",
    "current_setting", "pg_terminate_backend", "pg_cancel_backend", "query_to_xml",
    "generate_series",  # prevents trivially expensive cartesian blow-ups
    # our SECURITY DEFINER helpers: only the service itself calls these, never generated SQL
    "log_chat", "session_is_active", "ai_user_access", "effective_page_permissions", "has_page_permission",
})


class SqlRejected(ValueError):
    """Raised when generated SQL fails validation. Message is safe to show the model."""


def _function_name(f: exp.Func) -> str:
    if isinstance(f, exp.Anonymous):
        return str(f.name).lower()
    return f.sql_name().lower()


def validate_sql(sql: str, max_rows: int = 500, allowed_tables: frozenset[str] | None = None) -> str:
    """Return a safe, row-limited version of `sql` or raise SqlRejected.

    `allowed_tables` narrows ALLOWED_TABLES to what this user's page rights permit.
    """
    allowed = ALLOWED_TABLES if allowed_tables is None else (ALLOWED_TABLES & allowed_tables)
    if not sql or not sql.strip():
        raise SqlRejected("Empty query.")
    cleaned = sql.strip().rstrip(";").strip()

    try:
        statements = [s for s in sqlglot.parse(cleaned, read="postgres") if s is not None]
    except sqlglot.errors.ParseError as e:
        raise SqlRejected(f"Could not parse SQL: {str(e).splitlines()[0]}") from e

    if len(statements) != 1:
        raise SqlRejected("Exactly one statement is allowed.")
    tree = statements[0]

    if not isinstance(tree, (exp.Select, exp.Union, exp.Intersect, exp.Except)):
        raise SqlRejected("Only SELECT queries are allowed.")

    for node_type in FORBIDDEN_NODES:
        if tree.find(node_type) is not None:
            raise SqlRejected(f"Forbidden operation: {node_type.__name__.upper()}.")

    for f in tree.find_all(exp.Func):
        name = _function_name(f)
        if name in FORBIDDEN_FUNCTIONS or name.startswith("pg_"):
            raise SqlRejected(f"Function not allowed: {name}.")

    cte_names = {cte.alias_or_name.lower() for cte in tree.find_all(exp.CTE)}
    for t in tree.find_all(exp.Table):
        name = t.name.lower()
        schema = (t.db or "").lower()
        if name in cte_names and not schema:
            continue
        if schema not in ("", "public"):
            raise SqlRejected(f"Schema not allowed: {schema}.")
        if name in ALLOWED_TABLES and name not in allowed:
            raise SqlRejected(
                f"Table not allowed: {name}. This user has no permission to read that data. "
                f"Tables this user may query: {', '.join(sorted(allowed)) or 'none'}."
            )
        if name not in allowed:
            raise SqlRejected(
                f"Table not allowed: {name}. Allowed tables: {', '.join(sorted(allowed)) or 'none'}."
            )

    # Re-emit from the AST (normalises comments / weird whitespace) and enforce a hard limit.
    inner = tree.sql(dialect="postgres", comments=False)
    return f"SELECT * FROM ({inner}) AS _q LIMIT {int(max_rows)}"
