ALTER TABLE runs DROP COLUMN victory_reward;
ALTER TABLE runs DROP COLUMN defeat_reward;
ALTER TABLE runs ADD COLUMN reward_per_church_level bigint
    CHECK (reward_per_church_level >= 0);
ALTER TABLE run_result_operations ADD COLUMN church_level integer NOT NULL DEFAULT 1
    CHECK (church_level BETWEEN 1 AND 1000);
