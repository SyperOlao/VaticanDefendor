from .errors import ApplicationError
from .ports import UnitOfWorkFactory
from ..domain.profiles import AvatarOption, validate_changes


def profile_response(row):
    return {"accountId": str(row.account_id), "version": row.version,
            "displayName": row.display_name, "avatarAssetKey": row.avatar_asset_key,
            "displayTimezoneIana": row.display_timezone_iana, "locale": row.locale}


class ProfileUseCases:
    def __init__(self, uow: UnitOfWorkFactory, avatars: tuple[AvatarOption,...]=(), media_base_url: str = ''):
        self.uow, self.avatars = uow, avatars
        self.media_base_url = media_base_url.rstrip('/')

    def available_avatars(self):
        options = []
        for avatar in self.avatars:
            option = {'assetKey': avatar.asset_key, 'title': avatar.title, 'symbol': avatar.symbol,
                      'backgroundColor': avatar.background_color}
            if self.media_base_url and avatar.image_key:
                option['imageUrl'] = f'{self.media_base_url}/{avatar.image_key}'
            options.append(option)
        return {'avatars': options}

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

    def display_names(self, account_ids):
        with self.uow() as work:
            names = work.profiles.display_names(account_ids)
            return {'players': [{'accountId': str(account_id), 'displayName': name}
                                for account_id, name in names.items()]}

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
