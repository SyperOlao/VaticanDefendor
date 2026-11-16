from .errors import ApplicationError


def profile_response(row):
    return {"accountId": str(row.account_id), "version": row.version,
            "displayName": row.display_name, "permanentCurrency": row.permanent_currency}


class ProfileUseCases:
    def __init__(self, uow):
        self.uow = uow

    def get(self, account_id):
        with self.uow() as work:
            row = work.profiles.get(account_id)
            if row is None:
                raise ApplicationError(404, "Profile not found")
            return profile_response(row)

    def rename(self, account_id, expected_version, display_name):
        name = display_name.strip()
        if not name:
            raise ApplicationError(422, "displayName is empty")
        if len(name.encode("utf-8")) > 120:
            raise ApplicationError(422, "displayName is too long in UTF-8")
        with self.uow() as work:
            row = work.profiles.rename(account_id, expected_version, name)
            if row is None:
                raise ApplicationError(409, "Profile version changed")
            return profile_response(row)


class HealthUseCases:
    def __init__(self, uow, schema_revision):
        self.uow = uow
        self.schema_revision = schema_revision

    def ready(self):
        with self.uow() as work:
            if work.health.schema_versions() != {self.schema_revision}:
                raise ApplicationError(503, "Database schema is not ready")
        return {"status": "ready", "schemaVersion": self.schema_revision}
