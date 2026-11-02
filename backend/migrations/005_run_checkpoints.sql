CREATE TABLE run_checkpoints (
    run_id uuid PRIMARY KEY REFERENCES runs(id),
    generation integer NOT NULL CHECK (generation > 0),
    schema_version integer NOT NULL CHECK (schema_version > 0),
    simulation_tick bigint NOT NULL CHECK (simulation_tick >= 0),
    snapshot_json text NOT NULL,
    sha256 bytea NOT NULL CHECK (octet_length(sha256) = 32),
    created_at timestamptz NOT NULL DEFAULT now()
);
