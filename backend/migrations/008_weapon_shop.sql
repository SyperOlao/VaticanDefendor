-- Preserve the existing revision floor while separating wallet and inventory revisions.
ALTER TABLE profiles ADD COLUMN wallet_revision bigint NOT NULL DEFAULT 1 CHECK (wallet_revision > 0);
ALTER TABLE profiles ADD COLUMN inventory_revision bigint NOT NULL DEFAULT 1 CHECK (inventory_revision > 0);
UPDATE profiles SET wallet_revision=version, inventory_revision=version;

-- The API seeds immutable definitions from the validated weapon catalog at startup.
-- Removed definitions remain resolvable by previously granted instances.
CREATE TABLE weapon_configurations (
    configuration_id varchar(80) PRIMARY KEY,
    configuration_json jsonb NOT NULL
);

CREATE INDEX purchase_operations_completed_offer_idx ON purchase_operations(account_id, offer_id)
    WHERE receipt_json->>'status'='Completed';
