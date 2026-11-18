from .errors import ApplicationError
from .ports import UnitOfWorkFactory


class HealthUseCases:
    def __init__(self, uow: UnitOfWorkFactory, revision: str):
        self.uow, self.revision = uow, revision
    def ready(self):
        with self.uow() as work:
            if work.health.revision() != self.revision:
                raise ApplicationError(503, "Database schema is not ready")
        return {"status": "ready", "schemaRevision": self.revision}
