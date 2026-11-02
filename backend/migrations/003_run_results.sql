ALTER TABLE runs DROP CONSTRAINT runs_status_check;
ALTER TABLE runs ADD CONSTRAINT runs_status_check
    CHECK (status IN ('pending', 'ready', 'failed', 'completed'));
ALTER TABLE runs ADD COLUMN completed_at timestamptz;
ALTER TABLE runs ADD COLUMN outcome varchar(16);
ALTER TABLE runs ADD COLUMN permanent_reward bigint NOT NULL DEFAULT 0
    CHECK (permanent_reward >= 0);
ALTER TABLE runs ADD COLUMN victory_reward bigint CHECK (victory_reward >= 0);
ALTER TABLE runs ADD COLUMN defeat_reward bigint CHECK (defeat_reward >= 0);

CREATE TABLE run_result_operations (
    run_id uuid PRIMARY KEY REFERENCES runs(id),
    account_id uuid NOT NULL REFERENCES accounts(id),
    server_id uuid NOT NULL REFERENCES game_servers(id),
    generation integer NOT NULL,
    content_release_id varchar(128) NOT NULL,
    payload_hash bytea NOT NULL,
    outcome varchar(16) NOT NULL,
    permanent_reward bigint NOT NULL CHECK (permanent_reward >= 0),
    profile_version integer NOT NULL CHECK (profile_version > 0),
    applied_at timestamptz NOT NULL DEFAULT now()
);
