from .repositories.state import StateRepository
from .repositories.health import HealthRepository
from .repositories.catalog import CatalogRepository
from .repositories.mock_payments import MockPaymentsRepository


class UnitOfWork:
    def __init__(self, database):
        self.database = database

    def __enter__(self):
        self.transaction = self.database.transaction()
        self.session = self.transaction.__enter__()
        self.repository = StateRepository(self.session)
        self.health = HealthRepository(self.session)
        self.catalog = CatalogRepository(self.session)
        self.mock = MockPaymentsRepository(self.session)
        return self

    def __exit__(self, *exception):
        return self.transaction.__exit__(*exception)


class PostgresUnitOfWorkFactory:
    def __init__(self, database):
        self.database = database

    def __call__(self):
        return UnitOfWork(self.database)
