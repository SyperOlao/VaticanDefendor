from datetime import timedelta

from sqlalchemy import func, select
from sqlalchemy.dialects.postgresql import insert

from ..tables import ContentRelease, GameServer, Ruleset, Run, RunCheckpoint, RunResultOperation


class RunsRepository:
    def __init__(self, session):
        self.session = session

    def server(self, server_id, lock=False):
        return self.session.get(GameServer, server_id, with_for_update=lock, populate_existing=lock)

    def register_server(self, **values):
        statement = insert(GameServer).values(**values)
        # Only an unassigned process can be registered, including concurrent first registrations.
        changed = {key: getattr(statement.excluded, key) for key in values if key != "id"}
        return self.session.scalar(statement.on_conflict_do_update(
            index_elements=[GameServer.id], set_=changed, where=GameServer.current_run_id.is_(None)
        ).returning(GameServer.id))

    def ensure_ruleset(self, release_id):
        release = self.session.get(ContentRelease, release_id)
        self.session.execute(insert(Ruleset).values(
            id=release_id, code=release_id, version=1, status="PUBLISHED",
            content_release_id=release_id if release is not None else None,
        ).on_conflict_do_nothing(index_elements=[Ruleset.id]))

    def available_server(self):
        return self.session.scalar(select(GameServer).where(
            GameServer.status == "ready", GameServer.current_run_id.is_(None),
            GameServer.last_seen > func.now() - timedelta(seconds=15)
        ).order_by(GameServer.id).limit(1).with_for_update(skip_locked=True).execution_options(populate_existing=True))

    def has_available_server(self):
        # A heartbeat can briefly lock the only free process. This is not pool exhaustion.
        return self.session.scalar(select(select(GameServer.id).where(
            GameServer.status == "ready", GameServer.current_run_id.is_(None),
            GameServer.last_seen > func.now() - timedelta(seconds=15)
        ).exists()))

    def get(self, run_id, lock=False):
        return self.session.get(Run, run_id, with_for_update=lock, populate_existing=lock)

    def active(self, account_id):
        return self.session.scalar(select(Run).where(Run.account_id == account_id,
                                                     Run.status.in_(["pending", "ready"]))
                                   .order_by(Run.created_at.desc()).limit(1))

    def create(self, **values):
        values.setdefault("ruleset_id", values["content_release_id"])
        run = Run(**values)
        self.session.add(run)
        self.session.flush()  # The server's current_run_id references this row.
        return run

    def ready_pair(self, run_id, account_id):
        return self.session.execute(select(Run, GameServer).join(GameServer, GameServer.id == Run.server_id)
                                    .where(Run.id == run_id, Run.account_id == account_id)).first()

    def checkpoint(self, run_id, lock=False):
        return self.session.get(RunCheckpoint, run_id, with_for_update=lock, populate_existing=lock)

    def save_checkpoint(self, **values):
        statement = insert(RunCheckpoint).values(**values)
        changes = {key: getattr(statement.excluded, key) for key in values if key != "run_id"}
        self.session.execute(statement.on_conflict_do_update(
            index_elements=[RunCheckpoint.run_id], set_={**changes, "created_at": func.now()}))

    def result(self, run_id):
        return self.session.get(RunResultOperation, run_id)

    def record_result(self, **values):
        self.session.add(RunResultOperation(**values))
