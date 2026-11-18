from sqlalchemy import ForeignKey,String
from sqlalchemy.dialects.postgresql import JSONB
from sqlalchemy.orm import Mapped,mapped_column
from ..base import Base,CreatedAtMixin


class LocalRunPolicy(CreatedAtMixin,Base):
    __tablename__='local_run_policies'
    id: Mapped[str]=mapped_column(String(128),primary_key=True)
    ruleset_id: Mapped[str]=mapped_column(ForeignKey('rulesets.id'))
    content_release_id: Mapped[str]=mapped_column(ForeignKey('content_releases.id'))
    snapshot: Mapped[dict]=mapped_column(JSONB)
