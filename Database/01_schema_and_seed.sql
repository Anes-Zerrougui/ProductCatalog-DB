-- =====================================================================
-- BMT-311 Product Catalog Management System
-- 01_schema_and_seed.sql
-- Target: PostgreSQL 13+ (database: productcatalog_db)
-- Safe to re-run: it drops and recreates everything.
-- =====================================================================

CREATE EXTENSION IF NOT EXISTS pgcrypto;   -- password hashing (bcrypt) for seed users

-- ---------------------------------------------------------------------
-- 0. Clean start
-- ---------------------------------------------------------------------
DROP VIEW IF EXISTS v_product_list;
DROP TABLE IF EXISTS product_audit_log, product_tag, product_attribute,
                     product_image, product, product_group, tag,
                     app_user, role CASCADE;
DROP PROCEDURE IF EXISTS sp_adjust_stock(integer, integer, integer);
DROP FUNCTION IF EXISTS fn_product_audit();
DROP FUNCTION IF EXISTS fn_set_updated_at();

-- ---------------------------------------------------------------------
-- 1. Tables (physical design)
-- ---------------------------------------------------------------------

CREATE TABLE role (
    role_id      integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    role_name    varchar(30)  NOT NULL UNIQUE,
    description  varchar(200)
);

CREATE TABLE app_user (
    user_id        integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    username       varchar(50)  NOT NULL UNIQUE
                   CHECK (char_length(username) >= 3),
    email          varchar(100) NOT NULL UNIQUE
                   CHECK (email ~ '^[^@\s]+@[^@\s]+\.[^@\s]+$'),
    password_hash  varchar(255) NOT NULL,
    full_name      varchar(100) NOT NULL,
    is_active      boolean      NOT NULL DEFAULT true,
    created_at     timestamptz  NOT NULL DEFAULT now(),
    role_id        integer      NOT NULL
                   REFERENCES role (role_id) ON DELETE RESTRICT
);

CREATE TABLE product_group (
    group_id     integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    group_name   varchar(100) NOT NULL UNIQUE
                 CHECK (char_length(trim(group_name)) > 0),
    description  varchar(500),
    created_at   timestamptz  NOT NULL DEFAULT now(),
    updated_at   timestamptz  NOT NULL DEFAULT now()
);

CREATE TABLE product (
    product_id      integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    product_name    varchar(200)  NOT NULL
                    CHECK (char_length(trim(product_name)) > 0),
    description     text,
    sku             varchar(50)   NOT NULL,
    price           numeric(12,2) NOT NULL CHECK (price > 0),
    stock_quantity  integer       NOT NULL DEFAULT 0 CHECK (stock_quantity >= 0),
    status          varchar(20)   NOT NULL DEFAULT 'Draft'
                    CHECK (status IN ('Draft', 'Active', 'Inactive')),
    is_deleted      boolean       NOT NULL DEFAULT false,
    created_at      timestamptz   NOT NULL DEFAULT now(),
    updated_at      timestamptz   NOT NULL DEFAULT now(),
    group_id        integer       NOT NULL
                    REFERENCES product_group (group_id) ON DELETE RESTRICT
);

-- SKU must be unique only among products that are not soft-deleted
CREATE UNIQUE INDEX ux_product_sku_active ON product (sku) WHERE is_deleted = false;
CREATE INDEX ix_product_group_id ON product (group_id);
CREATE INDEX ix_product_status   ON product (status);

CREATE TABLE product_image (
    image_id       integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    product_id     integer      NOT NULL
                   REFERENCES product (product_id) ON DELETE CASCADE,
    image_url      varchar(500) NOT NULL,
    is_primary     boolean      NOT NULL DEFAULT false,
    display_order  integer      NOT NULL DEFAULT 0 CHECK (display_order >= 0),
    CONSTRAINT uq_image_order UNIQUE (product_id, display_order)
                   DEFERRABLE INITIALLY DEFERRED
);

-- At most one primary image per product
CREATE UNIQUE INDEX ux_image_one_primary ON product_image (product_id) WHERE is_primary;

CREATE TABLE product_attribute (
    attribute_id     integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    product_id       integer      NOT NULL
                     REFERENCES product (product_id) ON DELETE CASCADE,
    attribute_name   varchar(100) NOT NULL,
    attribute_value  varchar(255) NOT NULL,
    CONSTRAINT uq_attribute_name UNIQUE (product_id, attribute_name)
                     DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE tag (
    tag_id       integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tag_name     varchar(50)  NOT NULL UNIQUE,
    description  varchar(200)
);

CREATE TABLE product_tag (
    product_id  integer NOT NULL REFERENCES product (product_id) ON DELETE CASCADE,
    tag_id      integer NOT NULL REFERENCES tag (tag_id)         ON DELETE CASCADE,
    PRIMARY KEY (product_id, tag_id)
);

CREATE TABLE product_audit_log (
    log_id       integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    product_id   integer       NOT NULL
                 REFERENCES product (product_id) ON DELETE RESTRICT,
    changed_by   integer
                 REFERENCES app_user (user_id) ON DELETE SET NULL,
    old_price    numeric(12,2),
    new_price    numeric(12,2),
    old_stock    integer,
    new_stock    integer,
    changed_at   timestamptz   NOT NULL DEFAULT now()
);

CREATE INDEX ix_audit_product ON product_audit_log (product_id, changed_at DESC);

-- ---------------------------------------------------------------------
-- 2. View (used by the product list / search screen)
-- ---------------------------------------------------------------------
CREATE VIEW v_product_list AS
SELECT p.product_id,
       p.product_name,
       p.sku,
       p.price,
       p.stock_quantity,
       p.status,
       g.group_id,
       g.group_name,
       (SELECT i.image_url
          FROM product_image i
         WHERE i.product_id = p.product_id AND i.is_primary
         LIMIT 1) AS primary_image_url,
       (SELECT string_agg(t.tag_name, ', ' ORDER BY t.tag_name)
          FROM product_tag pt
          JOIN tag t ON t.tag_id = pt.tag_id
         WHERE pt.product_id = p.product_id) AS tags
FROM product p
JOIN product_group g ON g.group_id = p.group_id
WHERE p.is_deleted = false;

-- ---------------------------------------------------------------------
-- 3. Triggers
-- ---------------------------------------------------------------------

-- 3a. Keep updated_at correct automatically
CREATE FUNCTION fn_set_updated_at() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at := now();
    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_product_updated_at
    BEFORE UPDATE ON product
    FOR EACH ROW EXECUTE FUNCTION fn_set_updated_at();

CREATE TRIGGER trg_product_group_updated_at
    BEFORE UPDATE ON product_group
    FOR EACH ROW EXECUTE FUNCTION fn_set_updated_at();

-- 3b. Audit trigger: log every real change of price or stock.
--     The application (or sp_adjust_stock) can say who made the change with:
--        SELECT set_config('app.current_user_id', '<user_id>', true);
CREATE FUNCTION fn_product_audit() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_user integer;
BEGIN
    v_user := NULLIF(current_setting('app.current_user_id', true), '')::integer;

    INSERT INTO product_audit_log
           (product_id, changed_by, old_price, new_price, old_stock, new_stock)
    VALUES (NEW.product_id, v_user, OLD.price, NEW.price,
            OLD.stock_quantity, NEW.stock_quantity);

    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_product_audit
    AFTER UPDATE OF price, stock_quantity ON product
    FOR EACH ROW
    WHEN (OLD.price IS DISTINCT FROM NEW.price
          OR OLD.stock_quantity IS DISTINCT FROM NEW.stock_quantity)
    EXECUTE FUNCTION fn_product_audit();

-- ---------------------------------------------------------------------
-- 4. Stored procedure: adjust stock safely
--    p_amount > 0 adds stock, p_amount < 0 removes stock.
--    Refuses to let stock go below zero. The audit trigger logs the change.
-- ---------------------------------------------------------------------
CREATE PROCEDURE sp_adjust_stock(
    p_product_id integer,
    p_amount     integer,
    p_user_id    integer DEFAULT NULL
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_current integer;
    v_new     integer;
BEGIN
    IF p_amount = 0 THEN
        RAISE EXCEPTION 'Stock adjustment amount must not be zero.';
    END IF;

    IF p_user_id IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM app_user WHERE user_id = p_user_id) THEN
        RAISE EXCEPTION 'User % does not exist.', p_user_id;
    END IF;

    -- Lock the row so two people cannot change the same stock at the same time
    SELECT stock_quantity INTO v_current
      FROM product
     WHERE product_id = p_product_id AND is_deleted = false
       FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Product % was not found.', p_product_id;
    END IF;

    v_new := v_current + p_amount;

    IF v_new < 0 THEN
        RAISE EXCEPTION 'Not enough stock: current %, requested change %.',
                        v_current, p_amount;
    END IF;

    -- Tell the audit trigger who is making the change (valid until the transaction ends)
    PERFORM set_config('app.current_user_id', COALESCE(p_user_id::text, ''), true);

    UPDATE product
       SET stock_quantity = v_new
     WHERE product_id = p_product_id;
END;
$$;

-- ---------------------------------------------------------------------
-- 5. Seed data (initial data loading)
--    Demo passwords: admin = Admin123!   editor1 = Editor123!   viewer1 = Viewer123!
-- ---------------------------------------------------------------------

INSERT INTO role (role_name, description) VALUES
    ('Admin',  'Full access, including user management'),
    ('Editor', 'Can create, update and delete products'),
    ('Viewer', 'Read-only access: list and search');

INSERT INTO app_user (username, email, password_hash, full_name, role_id)
SELECT v.username, v.email, crypt(v.pwd, gen_salt('bf', 10)), v.full_name, r.role_id
FROM (VALUES
    ('admin',   'admin@example.com',   'Admin123!',  'System Administrator', 'Admin'),
    ('editor1', 'editor1@example.com', 'Editor123!', 'Demo Editor',          'Editor'),
    ('viewer1', 'viewer1@example.com', 'Viewer123!', 'Demo Viewer',          'Viewer')
) AS v(username, email, pwd, full_name, role_name)
JOIN role r ON r.role_name = v.role_name;

INSERT INTO product_group (group_name, description) VALUES
    ('Laptops',     'Portable computers'),
    ('Smartphones', 'Mobile phones'),
    ('Monitors',    'Computer displays'),
    ('Accessories', 'Mice, keyboards and other add-ons');

INSERT INTO product (product_name, description, sku, price, stock_quantity, status, group_id)
SELECT v.name, v.descr, v.sku, v.price, v.stock, v.status, g.group_id
FROM (VALUES
    ('Dell XPS 13',          '13-inch ultrabook for work and travel', 'LAP-DELL-XPS13', 1299.00, 15, 'Active',   'Laptops'),
    ('Lenovo ThinkPad T14',  'Business laptop with strong keyboard',  'LAP-LEN-T14',    1149.50,  8, 'Active',   'Laptops'),
    ('iPhone 15',            'Smartphone with 6.1-inch display',      'PHN-APL-IP15',    899.00, 25, 'Active',   'Smartphones'),
    ('Samsung Galaxy S24',   'Android flagship smartphone',           'PHN-SAM-S24',     799.99,  0, 'Inactive', 'Smartphones'),
    ('LG UltraWide 34',      '34-inch ultrawide monitor',             'MON-LG-34UW',     429.00, 12, 'Active',   'Monitors'),
    ('Dell UltraSharp 27',   '27-inch IPS monitor',                   'MON-DEL-U27',     389.90,  7, 'Draft',    'Monitors'),
    ('Logitech MX Master 3S','Wireless ergonomic mouse',              'ACC-LOG-MX3S',     99.99, 40, 'Active',   'Accessories'),
    ('Mechanical Keyboard K2','Compact wireless mechanical keyboard', 'ACC-KEY-K2',       79.00, 22, 'Active',   'Accessories')
) AS v(name, descr, sku, price, stock, status, grp)
JOIN product_group g ON g.group_name = v.grp;

INSERT INTO product_image (product_id, image_url, is_primary, display_order)
SELECT p.product_id, v.url, v.is_primary, v.ord
FROM (VALUES
    ('LAP-DELL-XPS13', 'https://placehold.co/600x400?text=XPS+13+Front', true,  0),
    ('LAP-DELL-XPS13', 'https://placehold.co/600x400?text=XPS+13+Side',  false, 1),
    ('PHN-APL-IP15',   'https://placehold.co/600x400?text=iPhone+15',    true,  0),
    ('MON-LG-34UW',    'https://placehold.co/600x400?text=LG+UltraWide', true,  0),
    ('ACC-LOG-MX3S',   'https://placehold.co/600x400?text=MX+Master',    true,  0)
) AS v(sku, url, is_primary, ord)
JOIN product p ON p.sku = v.sku;

INSERT INTO product_attribute (product_id, attribute_name, attribute_value)
SELECT p.product_id, v.attr, v.val
FROM (VALUES
    ('LAP-DELL-XPS13', 'RAM',        '16 GB'),
    ('LAP-DELL-XPS13', 'CPU',        'Intel Core i7'),
    ('LAP-DELL-XPS13', 'Screen',     '13.4 inch'),
    ('PHN-APL-IP15',   'Storage',    '128 GB'),
    ('PHN-APL-IP15',   'Color',      'Black'),
    ('MON-LG-34UW',    'Screen',     '34 inch'),
    ('MON-LG-34UW',    'Resolution', '3440x1440'),
    ('ACC-LOG-MX3S',   'Connection', 'Bluetooth'),
    ('ACC-LOG-MX3S',   'DPI',        '8000')
) AS v(sku, attr, val)
JOIN product p ON p.sku = v.sku;

INSERT INTO tag (tag_name, description) VALUES
    ('business', 'For office and professional use'),
    ('portable', 'Easy to carry'),
    ('wireless', 'No cables needed'),
    ('premium',  'High-end products');

INSERT INTO product_tag (product_id, tag_id)
SELECT p.product_id, t.tag_id
FROM (VALUES
    ('LAP-DELL-XPS13', 'business'),
    ('LAP-DELL-XPS13', 'portable'),
    ('LAP-DELL-XPS13', 'premium'),
    ('LAP-LEN-T14',    'business'),
    ('LAP-LEN-T14',    'portable'),
    ('PHN-APL-IP15',   'premium'),
    ('PHN-APL-IP15',   'portable'),
    ('MON-LG-34UW',    'premium'),
    ('ACC-LOG-MX3S',   'wireless'),
    ('ACC-KEY-K2',     'wireless')
) AS v(sku, tag_name)
JOIN product p ON p.sku = v.sku
JOIN tag t     ON t.tag_name = v.tag_name;
