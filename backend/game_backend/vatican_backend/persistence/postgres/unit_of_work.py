from .repositories.content import ContentRepository
from .repositories.health import HealthRepository
from .repositories.profiles import ProfilesRepository
from .repositories.runs import RunsRepository
from .repositories.shop import ShopRepository
from .repositories.weapons import WeaponsRepository


class UnitOfWork:
    """A fresh instance per operation; all repositories share one ORM transaction."""

    def __init__(self, database):
        self.database = database

    def __enter__(self):
        self._transaction = self.database.transaction()
        session = self._transaction.__enter__()
        self.content = ContentRepository(session)
        self.health = HealthRepository(session)
        self.profiles = ProfilesRepository(session)
        self.runs = RunsRepository(session)
        self.shop = ShopRepository(session)
        self.weapons = WeaponsRepository(session)
        return self

    def __exit__(self, *exception):
        return self._transaction.__exit__(*exception)
