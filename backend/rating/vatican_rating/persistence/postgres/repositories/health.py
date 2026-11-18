from sqlalchemy import text

class HealthRepository:
    def __init__(self, session):
        self.session = session
    def revision(self):
        return self.session.scalar(text(
            'SELECT CASE WHEN count(*) = 1 THEN min(version_num) END FROM alembic_version'))
