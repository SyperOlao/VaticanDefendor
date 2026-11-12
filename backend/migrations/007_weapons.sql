CREATE TABLE owned_weapons (
    instance_id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES accounts(id),
    configuration_id varchar(80) NOT NULL,
    level integer NOT NULL DEFAULT 1 CHECK (level >= 1),
    experience integer NOT NULL DEFAULT 0 CHECK (experience >= 0),
    grant_key varchar(80) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(account_id, grant_key)
);
CREATE INDEX owned_weapons_account_idx ON owned_weapons(account_id);
CREATE TABLE weapon_loadouts (
    account_id uuid NOT NULL REFERENCES accounts(id),
    slot integer NOT NULL CHECK (slot BETWEEN 0 AND 2),
    instance_id uuid NOT NULL REFERENCES owned_weapons(instance_id),
    PRIMARY KEY(account_id, slot),
    UNIQUE(account_id, instance_id)
);
ALTER TABLE runs ADD COLUMN weapon_snapshot jsonb;
