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
