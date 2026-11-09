"""Schema description given to the LLM. Kept static + hand-annotated: it is part of
the cached system prompt, and business meaning matters more than raw DDL."""

SCHEMA_DESCRIPTION = """
Tables (PostgreSQL). All rows are already restricted to the current user's organization.

customers(id uuid PK, name text, email text, city text, country text, created_at timestamptz)

products(id uuid PK, sku text, name text, category text  -- e.g. Electronics, Furniture, Stationery, Apparel,
         unit_price numeric(12,2)  -- INR, stock_quantity int, created_at timestamptz)

orders(id uuid PK, customer_id uuid FK->customers.id,
       status text  -- one of Pending, Paid, Shipped, Cancelled,
       total_amount numeric(14,2)  -- INR, ordered_at timestamptz)

order_items(id uuid PK, order_id uuid FK->orders.id, product_id uuid FK->products.id,
            quantity int, unit_price numeric(12,2)  -- price at time of order)

org_users(id uuid, full_name text, email text,
          role text  -- one of Admin, User,
          created_at timestamptz, last_login_at timestamptz  -- NULL = never signed in)
  -- The people who log in to this ERP (staff accounts of this organization). Read-only view.

Users vs customers (important):
- "users", "staff", "employees", "team members", "accounts", "admins", "who can log in" -> org_users.
- "customers", "clients", "buyers" -> customers. Customers place orders; users operate the ERP.
- Never answer a question about users with customer data, or vice versa.
- Orders are not linked to users (there is no "created by"), so questions like "orders per user"
  can't be answered; say so instead of substituting customers.

Business rules:
- Revenue = SUM(orders.total_amount) excluding status 'Cancelled'.
- "Low stock" means stock_quantity < 10.
""".strip()
