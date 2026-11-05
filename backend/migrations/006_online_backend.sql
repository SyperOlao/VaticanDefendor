CREATE TABLE account_logins (
    login varchar(64) PRIMARY KEY,
    account_id uuid NOT NULL UNIQUE REFERENCES accounts(id),
    password_hash text NOT NULL,
    recovery_hash bytea NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE auth_sessions (
    id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES accounts(id),
    access_hash bytea NOT NULL UNIQUE,
    access_expires_at timestamptz NOT NULL,
    refresh_hash bytea NOT NULL UNIQUE,
    refresh_expires_at timestamptz NOT NULL,
    revoked_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX auth_sessions_account_idx ON auth_sessions(account_id);
CREATE TABLE auth_attempts (
    key_hash bytea PRIMARY KEY,
    window_start timestamptz NOT NULL DEFAULT now(),
    attempts integer NOT NULL
);
CREATE TABLE content_releases (
    id varchar(128) PRIMARY KEY,
    schema_version integer NOT NULL,
    payload_json text NOT NULL,
    payload_sha256 varchar(64) NOT NULL,
    published_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE inventory (
    account_id uuid NOT NULL REFERENCES accounts(id),
    item_id varchar(64) NOT NULL,
    quantity integer NOT NULL CHECK (quantity >= 0),
    PRIMARY KEY (account_id, item_id)
);
CREATE TABLE purchase_operations (
    account_id uuid NOT NULL REFERENCES accounts(id),
    operation_id uuid NOT NULL,
    offer_id varchar(64) NOT NULL,
    catalog_version varchar(64) NOT NULL,
    receipt_json jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, operation_id)
);
