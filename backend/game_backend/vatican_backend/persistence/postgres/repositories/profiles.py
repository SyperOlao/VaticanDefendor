from sqlalchemy import func, update

from ..tables import Profile


class ProfilesRepository:
    def __init__(self, session):
        self.session = session

    def get(self, account_id, lock=False):
        return self.session.get(Profile, account_id, with_for_update=lock, populate_existing=lock)

    def rename(self, account_id, expected_version, name):
        return self.session.scalar(update(Profile).where(
            Profile.account_id == account_id, Profile.version == expected_version
        ).values(display_name=name, version=Profile.version + 1, updated_at=func.now())
            .returning(Profile).execution_options(populate_existing=True))

    def change_currency(self, account_id, delta, inventory_delta=0):
        return self.session.scalar(update(Profile).where(Profile.account_id == account_id).values(
            permanent_currency=Profile.permanent_currency + delta, version=Profile.version + 1,
            wallet_revision=Profile.wallet_revision + 1,
            inventory_revision=Profile.inventory_revision + inventory_delta, updated_at=func.now()
        ).returning(Profile).execution_options(populate_existing=True))

    def bump_inventory(self, account_id, bump_version=False):
        values = {"inventory_revision": Profile.inventory_revision + 1}
        if bump_version:
            values["version"] = Profile.version + 1
        self.session.execute(update(Profile).where(Profile.account_id == account_id).values(**values))
