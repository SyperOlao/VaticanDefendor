from .errors import ApplicationError
from .ports import UnitOfWorkFactory


class WeaponDefinitionsUseCases:
    def __init__(self, uow: UnitOfWorkFactory, catalog: list[dict]):
        self.uow, self.catalog = uow, catalog

    def register_catalog(self):
        with self.uow() as work:
            for definition in self.catalog:
                if work.weapons.register(definition) != definition:
                    raise ValueError("Weapon configuration is immutable; use a new Id")

    def get(self):
        with self.uow() as work:
            return work.weapons.definitions()

    def media_model(self, configuration_id: str) -> str:
        for definition in self.get():
            if definition["Id"] == configuration_id:
                return ("sword", "spear", "axe")[definition["ModelIndex"]]
        raise ApplicationError(404, "Weapon configuration not found")
