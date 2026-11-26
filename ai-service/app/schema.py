"""Schema description given to the LLM. Kept static + hand-annotated: it is part of
the cached system prompt, and business meaning matters more than raw DDL."""

SCHEMA_DESCRIPTION = """
Tables (PostgreSQL). All rows are already restricted to the current user's organization.

customers(id uuid PK, name text, email text, city text, country text, created_at timestamptz,
          phone text, contact_person text, tax_id text  -- GSTIN, address text, state text, postal_code text,
          payment_terms_days int)

products(id uuid PK, sku text, name text, category text  -- e.g. Electronics, Furniture, Stationery, Apparel,
         unit_price numeric(12,2)  -- sales price in INR, stock_quantity int  -- total over all stores,
         item_type text  -- one of 'Finished Good', 'Semi-Finished', 'Raw Material', 'Consumable',
         description text, hsn_code text, tax_rate numeric(5,2)  -- GST %, standard_cost numeric(12,2),
         reorder_level int, lead_time_days int, drawing_no text, is_active bool, created_at timestamptz)
  -- Called "items" in the app (Master > Inventory > Items).

orders(id uuid PK, customer_id uuid FK->customers.id,
       status text  -- one of Pending, Paid, Shipped, Cancelled,
       total_amount numeric(14,2)  -- INR, ordered_at timestamptz)

order_items(id uuid PK, order_id uuid FK->orders.id, product_id uuid FK->products.id,
            quantity int, unit_price numeric(12,2)  -- price at time of order)

org_users(id uuid, full_name text, email text,
          role text  -- one of SuperAdmin, Admin, Manager, User,
          created_at timestamptz, last_login_at timestamptz  -- NULL = never signed in)
  -- The people who log in to this ERP (staff accounts of this organization). Read-only view.

Sales flow (make-to-order): customer P.O. -> work order (production) -> sales invoice.
customer_pos(id uuid PK, number text  -- ours, e.g. CPO-00001, customer_id uuid FK->customers.id,
             customer_po_no text  -- the customer's own P.O. number, po_date date, delivery_date date,
             status text  -- one of Draft, Confirmed, Completed, Cancelled,
             sub_total numeric, tax_total numeric, grand_total numeric  -- INR incl. GST, created_at timestamptz)
customer_po_lines(id uuid PK, customer_po_id uuid FK->customer_pos.id, line_no int, product_id uuid FK->products.id,
                  quantity int, rate numeric, tax_rate numeric, amount numeric  -- qty x rate, tax_amount numeric,
                  produced_qty int, invoiced_qty int)
work_orders(id uuid PK, number text  -- e.g. WO-00001, customer_po_id uuid FK->customer_pos.id  -- NULL = made for stock,
            customer_po_line_id uuid, product_id uuid FK->products.id  -- the item produced, quantity int,
            posting_date date, status text  -- Draft (planned), Submitted (= production completed), Cancelled,
            material_cost numeric, other_cost numeric  -- labour/overhead, total_cost numeric, unit_cost numeric)
work_order_materials(id uuid PK, work_order_id uuid FK->work_orders.id, product_id uuid FK->products.id,
                     quantity int, rate numeric, amount numeric)  -- raw materials consumed
sales_invoices(id uuid PK, number text  -- e.g. SI-00001, customer_id uuid FK->customers.id,
               customer_po_id uuid FK->customer_pos.id, invoice_date date, due_date date,
               status text  -- Draft, Submitted (= issued), Cancelled, billing_name text, billing_state text,
               sub_total numeric, discount_total numeric, tax_total numeric, round_off numeric,
               grand_total numeric  -- INR incl. GST, cost_of_goods numeric, created_at timestamptz)
sales_invoice_lines(id uuid PK, sales_invoice_id uuid FK->sales_invoices.id, line_no int, product_id uuid FK->products.id,
                    quantity int, rate numeric, discount_percent numeric, amount numeric  -- taxable value,
                    tax_rate numeric, tax_amount numeric, total numeric, cost_rate numeric)

Users vs customers (important):
- "users", "staff", "employees", "team members", "accounts", "admins", "who can log in" -> org_users.
- "customers", "clients", "buyers" -> customers. Customers place orders; users operate the ERP.
- Never answer a question about users with customer data, or vice versa.
- Orders are not linked to users (there is no "created by"), so questions like "orders per user"
  can't be answered; say so instead of substituting customers.

Business rules:
- Revenue = SUM(orders.total_amount) excluding status 'Cancelled'.
- "Low stock" means stock_quantity < 10 (or below the item's reorder_level when asked about reorder).
- Invoiced sales = SUM(sales_invoices.grand_total) WHERE status = 'Submitted'. Drafts and cancelled invoices don't count.
- Open / pending P.O.s = customer_pos.status = 'Confirmed'. Pending quantity of a line = quantity - invoiced_qty.
- Production done = work_orders with status = 'Submitted'. Gross margin of an invoice = sub_total - discount_total - cost_of_goods.
- Orders (orders table) and customer P.O.s are different documents; say which one an answer is about.
""".strip()
