from .repositories.state import StateRepository
from .repositories.health import HealthRepository
from .repositories.broker import BrokerRepository


class UnitOfWork:
    def __init__(self, database):
        self.database = database

    def __enter__(self):
        self.transaction = self.database.transaction()
        self.session = self.transaction.__enter__()
        self.repository = StateRepository(self.session)
        self.health = HealthRepository(self.session)
        self.broker = BrokerRepository(self.session)
        return self

    def __exit__(self, *exception):
        return self.transaction.__exit__(*exception)


class PostgresUnitOfWorkFactory:
    def __init__(self, database):
        self.database = database

    def __call__(self):
        return UnitOfWork(self.database)
