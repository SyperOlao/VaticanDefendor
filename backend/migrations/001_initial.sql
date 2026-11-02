CREATE TABLE accounts (
    id uuid PRIMARY KEY,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE guest_credentials (
    token_hash bytea PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES accounts(id),
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX guest_credentials_account_idx ON guest_credentials(account_id);
CREATE TABLE profiles (
    account_id uuid PRIMARY KEY REFERENCES accounts(id),
    version integer NOT NULL CHECK (version > 0),
    display_name varchar(32) NOT NULL,
    permanent_currency bigint NOT NULL DEFAULT 0 CHECK (permanent_currency >= 0),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE game_servers (
    id uuid PRIMARY KEY,
    host varchar(255) NOT NULL,
    port integer NOT NULL CHECK (port BETWEEN 1 AND 65535),
    content_release_id varchar(128) NOT NULL,
    protocol_version integer NOT NULL,
    generation integer NOT NULL DEFAULT 0,
    current_run_id uuid,
    status varchar(16) NOT NULL CHECK (status IN ('ready', 'assigned', 'occupied')),
    last_seen timestamptz NOT NULL
);
CREATE TABLE runs (
    id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES accounts(id),
    server_id uuid NOT NULL REFERENCES game_servers(id),
    content_release_id varchar(128) NOT NULL,
    profile_version integer NOT NULL,
    profile_display_name varchar(32) NOT NULL,
    generation integer NOT NULL,
    status varchar(16) NOT NULL CHECK (status IN ('pending', 'ready', 'failed')),
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX runs_account_idx ON runs(account_id, created_at DESC);
CREATE UNIQUE INDEX runs_live_account_idx ON runs(account_id) WHERE status IN ('pending', 'ready');
CREATE UNIQUE INDEX runs_live_server_idx ON runs(server_id) WHERE status IN ('pending', 'ready');
ALTER TABLE game_servers ADD CONSTRAINT game_servers_current_run_fk
    FOREIGN KEY (current_run_id) REFERENCES runs(id);
