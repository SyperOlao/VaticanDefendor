from dataclasses import dataclass, field
import os
from ..persistence.postgres.database import environment_url
from ..persistence.clickhouse.database import environment_url as warehouse_url


@dataclass(frozen=True)
class Settings:
    database_url: str=field(repr=False)
    service_secret: str=field(repr=False)
    clickhouse_url: str=field(repr=False)

    @classmethod
    def from_environment(cls):
        secret=os.environ['VD_SERVICE_SECRET']
        if len(secret)<32:raise ValueError('VD_SERVICE_SECRET must be at least 32 characters')
        return cls(environment_url('VD_ANALYTICS_INBOX'),secret,warehouse_url())
