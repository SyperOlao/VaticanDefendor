from sqlalchemy import select
from sqlalchemy.dialects.postgresql import insert
from ..tables import WeaponConfiguration


class WeaponDefinitionsRepository:
    def __init__(self, session):
        self.session = session

    def register(self, definition):
        self.session.execute(insert(WeaponConfiguration).values(
            configuration_id=definition["Id"], configuration_json=definition
        ).on_conflict_do_nothing(index_elements=[WeaponConfiguration.configuration_id]))
        return self.session.get(WeaponConfiguration, definition["Id"]).configuration_json

    def definitions(self):
        return list(self.session.scalars(select(WeaponConfiguration.configuration_json)
                                         .order_by(WeaponConfiguration.configuration_id)))
