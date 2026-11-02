ALTER TABLE game_servers DROP CONSTRAINT game_servers_status_check;
ALTER TABLE game_servers ADD CONSTRAINT game_servers_status_check
    CHECK (status IN ('ready', 'assigned', 'occupied', 'unavailable'));
