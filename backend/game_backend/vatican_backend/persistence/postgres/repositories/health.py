from sqlalchemy import Column, MetaData, String, Table, select

# Alembic owns this infrastructure table; it is not part of application metadata.
versions = Table("alembic_version", MetaData(), Column("version_num", String(32), primary_key=True))


class HealthRepository:
    def __init__(self, session):
        self.session = session

    def schema_versions(self):
        return set(self.session.scalars(select(versions.c.version_num)))
