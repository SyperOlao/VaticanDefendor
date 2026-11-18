from .repositories.inbox import InboxRepository
from .repositories.health import HealthRepository


class UnitOfWork:
    def __init__(self,database):self.database=database
    def __enter__(self):
        self._tx=self.database.transaction()
        session=self._tx.__enter__()
        self.repository=InboxRepository(session);self.health=HealthRepository(session)
        return self
    def __exit__(self,*exception):return self._tx.__exit__(*exception)


class PostgresUnitOfWorkFactory:
    def __init__(self,database):self.database=database
    def __call__(self):return UnitOfWork(self.database)
