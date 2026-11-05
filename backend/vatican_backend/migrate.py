import os
from pathlib import Path

import psycopg


def migrate(database_url: str | None = None) -> None:
    url = database_url or os.environ["VD_DATABASE_URL"]
    migration_dir = Path(__file__).resolve().parent.parent / "migrations"
    with psycopg.connect(url) as connection:
        with connection.transaction():
            connection.execute("SELECT pg_advisory_xact_lock(681750361)")
            connection.execute("CREATE TABLE IF NOT EXISTS schema_migrations (version varchar(64) PRIMARY KEY)")
            applied = {row[0] for row in connection.execute("SELECT version FROM schema_migrations")}
            for path in sorted(migration_dir.glob("[0-9][0-9][0-9]_*.sql")):
                if path.stem in applied:
                    continue
                connection.execute(path.read_text(encoding="utf-8"))
                connection.execute("INSERT INTO schema_migrations(version) VALUES (%s)", (path.stem,))


if __name__ == "__main__":
    migrate()
