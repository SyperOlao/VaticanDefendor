from .errors import ApplicationError
from .ports import UnitOfWorkFactory
from .validation import identifier
from ..domain.pagination import PageCursor
from ..domain.weapons import configuration_id


def page_limit(value):
    if type(value) is not int or not 1 <= value <= 100:
        raise ApplicationError(422, "Page size must be between 1 and 100")


class BrowseInventory:
    def __init__(self, uow: UnitOfWorkFactory):
        self.uow = uow

    @staticmethod
    def _cursor(value, scope, *, dated):
        if value is None:
            return None, None
        try:
            cursor = PageCursor.decode(value, scope)
            if cursor.revision is None:
                raise ValueError()
            if dated:
                position = cursor.dated_position()
            else:
                if len(cursor.position) != 1:
                    raise ValueError()
                position = cursor.position[0]
                if len(position) > 160 or "\x00" in position:
                    raise ValueError()
                position.encode("utf-8")
            return cursor, position
        except (ValueError, TypeError, AttributeError):
            raise ApplicationError(422, "Invalid inventory cursor") from None

    @staticmethod
    def _revision(repo, account, cursor):
        current = repo.state(account).revision
        if cursor is not None and cursor.revision != current:
            raise ApplicationError(409, "Inventory changed; restart pagination")
        return current

    def weapons(self, account_id, limit=50, after=None, configuration=None):
        identifier(account_id)
        page_limit(limit)
        if configuration is not None:
            try:
                configuration_id(configuration)
            except ValueError:
                raise ApplicationError(422, "Invalid weapon configuration") from None
        scope = ["inventory.weapons", str(account_id), configuration]
        cursor, position = self._cursor(after, scope, dated=True)
        with self.uow() as work:
            repo = work.repository
            repo.lock_player(account_id)
            repo.ensure(account_id)
            revision = self._revision(repo, account_id, cursor)
            rows = repo.weapon_page(account_id, limit + 1, position, configuration)
            visible = rows[:limit]
            next_cursor = None
            if len(rows) > limit:
                last = visible[-1]
                next_cursor = PageCursor(scope, [last.created_at.isoformat(), str(last.weapon.instance_id)], revision).encode()
            return {"Weapons": [{"InstanceId": str(row.weapon.instance_id), "ConfigurationId": row.weapon.configuration_id,
                                 "Level": row.weapon.level, "Experience": row.weapon.experience} for row in visible],
                    "Loadout": [str(identity) for identity in repo.loadout(account_id)], "Revision": revision,
                    "nextCursor": next_cursor}

    def items(self, account_id, limit=50, after=None, kind=None):
        identifier(account_id)
        page_limit(limit)
        if kind not in (None, "ITEM", "CASE"):
            raise ApplicationError(422, "Invalid item kind")
        scope = ["inventory.items", str(account_id), kind]
        cursor, position = self._cursor(after, scope, dated=False)
        with self.uow() as work:
            repo = work.repository
            repo.lock_player(account_id)
            repo.ensure(account_id)
            revision = self._revision(repo, account_id, cursor)
            rows = repo.item_page(account_id, limit + 1, position, kind)
            visible = rows[:limit]
            next_cursor = PageCursor(scope, [visible[-1].item_id], revision).encode() if len(rows) > limit else None
            return {"items": [{"itemId": row.item_id, "quantity": row.quantity} for row in visible],
                    "revision": revision, "nextCursor": next_cursor}
