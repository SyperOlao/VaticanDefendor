from .errors import ApplicationError
from .ports import UnitOfWorkFactory
from ..domain.profiles import AvatarOption, validate_changes


def profile_response(row):
    return {"accountId": str(row.account_id), "version": row.version,
            "displayName": row.display_name, "avatarAssetKey": row.avatar_asset_key,
            "displayTimezoneIana": row.display_timezone_iana, "locale": row.locale}


class ProfileUseCases:
    def __init__(self, uow: UnitOfWorkFactory, avatars: tuple[AvatarOption,...]=()):
        self.uow, self.avatars = uow, avatars

    def available_avatars(self):
        return {'avatars':[{'assetKey':a.asset_key,'title':a.title,'symbol':a.symbol,
                            'backgroundColor':a.background_color} for a in self.avatars]}

    def ensure(self, account_id):
        with self.uow() as work:
            work.profiles.ensure(account_id)
            return profile_response(work.profiles.details(account_id))

    def get(self, account_id):
        with self.uow() as work:
            row = work.profiles.details(account_id)
            if row is None:
                raise ApplicationError(404, "Profile not found")
            return profile_response(row)

    def rename(self, account_id, expected_version, display_name):
        return self.update(account_id,expected_version,{'displayName':display_name})

    def update(self, account_id, expected_version, changes):
        try:changes=validate_changes(expected_version,changes)
        except ValueError as error:raise ApplicationError(422,str(error)) from None
        avatar=changes.get('avatarAssetKey')
        if avatar is not None and avatar not in {a.asset_key for a in self.avatars}:
            raise ApplicationError(422,'Unknown avatarAssetKey')
        with self.uow() as work:
            row = work.profiles.update(account_id, expected_version, changes)
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
