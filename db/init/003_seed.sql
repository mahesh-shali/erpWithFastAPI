-- Demo seed data. Login: admin@demo.com / Admin@123
INSERT INTO organizations(id, name) VALUES
  ('11111111-1111-1111-1111-111111111111', 'Demo Corp'),
  ('22222222-2222-2222-2222-222222222222', 'Other Tenant Ltd');

-- BCrypt hash of "Admin@123" (work factor 11)
INSERT INTO users(organization_id, email, password_hash, full_name, role) VALUES
  ('11111111-1111-1111-1111-111111111111', 'admin@demo.com',
   '$2a$11$V76uTc8jRWnA6YTqhH2cveSrOfZ1roFle/sp8tWjHRJOtR/f.qBp.', 'Demo Admin', 'Admin');

INSERT INTO customers(organization_id, name, email, city, country)
SELECT '11111111-1111-1111-1111-111111111111',
       'Customer ' || g, 'customer' || g || '@example.com',
       (ARRAY['Bengaluru','Mumbai','Delhi','Pune','Chennai'])[1 + g % 5], 'India'
FROM generate_series(1, 200) g;

INSERT INTO products(organization_id, sku, name, category, unit_price, stock_quantity)
SELECT '11111111-1111-1111-1111-111111111111',
       'SKU-' || lpad(g::text, 4, '0'), 'Product ' || g,
       (ARRAY['Electronics','Furniture','Stationery','Apparel'])[1 + g % 4],
       round((random() * 9000 + 100)::numeric, 2), (random() * 500)::int
FROM generate_series(1, 100) g;

-- Orders over the past year
INSERT INTO orders(organization_id, customer_id, status, ordered_at)
SELECT '11111111-1111-1111-1111-111111111111', c.id,
       (ARRAY['Pending','Paid','Shipped','Cancelled'])[1 + (random()*3)::int],
       now() - (random() * interval '365 days')
FROM customers c, generate_series(1, 5);

INSERT INTO order_items(order_id, organization_id, product_id, quantity, unit_price)
SELECT o.id, o.organization_id, p.id, 1 + (random()*4)::int, p.unit_price
FROM orders o
CROSS JOIN LATERAL (SELECT id, unit_price FROM products
                    WHERE organization_id = o.organization_id
                    ORDER BY random() LIMIT 2) p;

UPDATE orders o SET total_amount = s.total
FROM (SELECT order_id, sum(quantity * unit_price) total FROM order_items GROUP BY order_id) s
WHERE s.order_id = o.id;

-- A row in another tenant, used by tests to prove RLS isolation
INSERT INTO customers(organization_id, name, city, country)
VALUES ('22222222-2222-2222-2222-222222222222', 'SECRET OTHER TENANT CUSTOMER', 'Paris', 'France');
