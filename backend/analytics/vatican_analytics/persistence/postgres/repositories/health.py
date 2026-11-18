from sqlalchemy import text

class HealthRepository:
    def __init__(self, session):
        self.session = session
    def revision(self):
        return self.session.scalar(text("SELECT version_num FROM alembic_version"))
