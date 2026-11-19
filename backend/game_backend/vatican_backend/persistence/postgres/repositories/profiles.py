from sqlalchemy import exists, func, select, update
from sqlalchemy.dialects.postgresql import insert
from ..tables import Profile, AccountPreference
from ....application.catalog import GameErrorCode
from ....application.errors import ApplicationError
from ....domain.profiles import ProfileDetails


DEFAULT_DISPLAY_NAME = "Player"


class ProfilesRepository:
    def __init__(self, session):
        self.session = session

    def ensure(self, account_id):
        self.session.execute(insert(Profile).values(account_id=account_id, version=1, display_name=DEFAULT_DISPLAY_NAME)
                             .on_conflict_do_nothing(index_elements=[Profile.account_id]))
        self.session.execute(insert(AccountPreference).values(account_id=account_id)
                             .on_conflict_do_nothing(index_elements=[AccountPreference.account_id]))
        return self.get(account_id)

    def get(self, account_id, lock=False):
        return self.session.get(Profile, account_id, with_for_update=lock, populate_existing=lock)

    def display_names(self, account_ids):
        rows = self.session.execute(select(Profile.account_id, Profile.display_name)
                                    .where(Profile.account_id.in_(account_ids)))
        return {account_id: name for account_id, name in rows}

    def details(self, account_id):
        row=self.session.execute(select(Profile,AccountPreference).outerjoin(AccountPreference,
            Profile.account_id==AccountPreference.account_id).where(Profile.account_id==account_id)).first()
        if row is None:return None
        profile,preferences=row
        return ProfileDetails(profile.account_id,profile.version,profile.display_name,
            preferences.avatar_asset_key if preferences else None,
            preferences.display_timezone_iana if preferences else 'UTC',preferences.locale if preferences else 'ru')

    def update(self, account_id, expected_version, changes):
        values={'version':Profile.version+1,'updated_at':func.now()}
        if 'displayName' in changes:
            profile = self.get(account_id, lock=True)
            if profile is None or profile.version != expected_version:
                return None
            display_name = changes['displayName']
            if display_name != profile.display_name:
                self._check_display_name(account_id, display_name)
                values['display_name'] = display_name
        changed=self.session.scalar(update(Profile).where(Profile.account_id==account_id,
            Profile.version==expected_version).values(**values).returning(Profile.account_id)
            .execution_options(synchronize_session=False))
        if changed is None:return None
        self.session.execute(insert(AccountPreference).values(account_id=account_id)
            .on_conflict_do_nothing(index_elements=[AccountPreference.account_id]))
        preferences={column:changes[key] for key,column in [('avatarAssetKey','avatar_asset_key'),
            ('displayTimezoneIana','display_timezone_iana'),('locale','locale')] if key in changes}
        if preferences:self.session.execute(update(AccountPreference).where(AccountPreference.account_id==account_id)
            .values(**preferences).execution_options(synchronize_session=False))
        self.session.expire_all()
        return self.details(account_id)

    def _check_display_name(self, account_id, display_name):
        if display_name.casefold() != DEFAULT_DISPLAY_NAME.casefold():
            name_key = func.lower(display_name)
            lock_key = func.hashtextextended(func.concat('profile-name:', name_key), 0)
            # Under READ COMMITTED the lookup sees the previous claimant after the lock is acquired.
            self.session.execute(select(func.pg_advisory_xact_lock(lock_key)))
            taken = self.session.scalar(select(exists().where(
                func.lower(Profile.display_name) == name_key, Profile.account_id != account_id)))
            if not taken:
                return
        raise ApplicationError(409, 'Display name is already taken', code=GameErrorCode.DISPLAY_NAME_TAKEN)

    def bump(self, account_id):
        return self.session.scalar(update(Profile).where(Profile.account_id == account_id).values(
            version=Profile.version + 1, updated_at=func.now()
        ).returning(Profile).execution_options(populate_existing=True))
