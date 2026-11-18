from .repositories.prestige import PrestigeRepository
from .repositories.content import ContentRepository
from .repositories.cases import CasesRepository
from .repositories.energy import EnergyRepository
from .repositories.level_rewards import LevelRewardsRepository
from .repositories.health import HealthRepository
from .repositories.profiles import ProfilesRepository
from .repositories.progression import ProgressionRepository
from .repositories.runs import RunsRepository
from .repositories.messaging import MessagingRepository
from .repositories.weapons import WeaponDefinitionsRepository
from .repositories.local_runs import LocalRunsRepository
from .repositories.rating_broker import RatingResultsRepository


class UnitOfWork:
    """A fresh instance per operation; all repositories share one ORM transaction."""

    def __init__(self, database):
        self.database = database

    def __enter__(self):
        self._transaction = self.database.transaction()
        session = self._transaction.__enter__()
        self.prestige = PrestigeRepository(session)
        self.content = ContentRepository(session)
        self.local_runs = LocalRunsRepository(session)
        self.cases = CasesRepository(session)
        self.energy = EnergyRepository(session)
        self.level_rewards = LevelRewardsRepository(session)
        self.health = HealthRepository(session)
        self.profiles = ProfilesRepository(session)
        self.progression = ProgressionRepository(session)
        self.runs = RunsRepository(session)
        self.messages = MessagingRepository(session)
        self.rating_results = RatingResultsRepository(session)
        self.weapons = WeaponDefinitionsRepository(session)
        return self

    def __exit__(self, *exception):
        return self._transaction.__exit__(*exception)


class PostgresUnitOfWorkFactory:
    def __init__(self, database):
        self.database = database

    def __call__(self):
        return UnitOfWork(self.database)
