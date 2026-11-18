from sqlalchemy.dialects.postgresql import insert
from sqlalchemy import update

from ..tables import ContentRelease, Ruleset


class ContentRepository:
    def __init__(self, session):
        self.session = session

    def get(self, release_id):
        return self.session.get(ContentRelease, release_id)

    def publish(self, **values):
        statement = insert(ContentRelease).values(**values).on_conflict_do_nothing(
            index_elements=[ContentRelease.id]).returning(ContentRelease.id)
        created = self.session.scalar(statement) is not None
        self.session.execute(update(Ruleset).where(Ruleset.id == values["id"], Ruleset.content_release_id.is_(None))
                             .values(content_release_id=values["id"]))
        return created
