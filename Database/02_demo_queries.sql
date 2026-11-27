-- =====================================================================
-- 02_demo_queries.sql
-- Run AFTER 01_schema_and_seed.sql. Run each block separately and
-- take screenshots for the report. Blocks marked EXPECTED ERROR must fail.
-- After a fresh run of script 01: product 1 = Dell XPS 13, user 2 = editor1.
-- =====================================================================

-- A. Check that the data was loaded
SELECT * FROM role;
SELECT user_id, username, email, role_id, is_active FROM app_user;
SELECT * FROM v_product_list ORDER BY product_id;

-- B. Search (same idea the UI will use: name or SKU, case-insensitive)
SELECT product_id, product_name, sku, price
FROM product
WHERE is_deleted = false
  AND (product_name ILIKE '%dell%' OR sku ILIKE '%dell%');

-- C. Password check in SQL (returns one row only if the password is right)
SELECT username
FROM app_user
WHERE username = 'admin'
  AND password_hash = crypt('Admin123!', password_hash);

-- D. Stored procedure: remove 3 items of product 1 as user 2
CALL sp_adjust_stock(1, -3, 2);

-- E. Trigger result: the change was logged automatically
SELECT l.log_id, p.product_name, u.username AS changed_by,
       l.old_stock, l.new_stock, l.old_price, l.new_price, l.changed_at
FROM product_audit_log l
JOIN product p       ON p.product_id = l.product_id
LEFT JOIN app_user u ON u.user_id = l.changed_by
ORDER BY l.log_id;

-- F. Trigger on a direct price change (no app user known, so changed_by is NULL)
UPDATE product SET price = 1249.00 WHERE product_id = 1;
SELECT * FROM product_audit_log ORDER BY log_id;

-- G. EXPECTED ERROR: procedure refuses negative stock
CALL sp_adjust_stock(1, -1000, 2);

-- H. EXPECTED ERROR: CHECK constraint (price must be > 0)
INSERT INTO product (product_name, sku, price, group_id)
VALUES ('Bad product', 'BAD-001', -5, 1);

-- I. EXPECTED ERROR: unique SKU among non-deleted products
INSERT INTO product (product_name, sku, price, group_id)
VALUES ('Duplicate', 'LAP-DELL-XPS13', 100, 1);

-- J. EXPECTED ERROR: a group that still has products cannot be deleted
DELETE FROM product_group WHERE group_id = 1;
