from ....domain.projection import rows
from uuid import UUID
from ..base import Base
from .. import tables


class ClickHouseProjection:
    def __init__(self,database):self.database=database

    def project(self,payload):
        # No incrementing aggregates: retries can physically repeat inserts.
        # Every row has a deterministic replacement key; read FINAL before SUM.
        with self.database.engine.connect() as connection:
            for name,values in rows(payload).items():
                # The HTTP driver's parameter escaper accepts UUID strings.
                parameters=[{key:str(value) if isinstance(value,UUID) else value for key,value in row.items()} for row in values]
                connection.execute(Base.metadata.tables[name].insert(),parameters)
