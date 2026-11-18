from datetime import datetime
from uuid import UUID
from sqlalchemy import String,Integer,DateTime,ForeignKey,CheckConstraint,UniqueConstraint,Index,text
from sqlalchemy.dialects.postgresql import JSONB
from sqlalchemy.orm import Mapped,mapped_column
from ..base import Base,CreatedAtMixin,IdentityMixin


class PrestigeConfiguration(CreatedAtMixin,Base):
    __tablename__='prestige_configurations'
    ruleset_id: Mapped[str]=mapped_column(ForeignKey('rulesets.id'),primary_key=True)
    snapshot: Mapped[dict]=mapped_column(JSONB)


class PrestigePurchase(IdentityMixin,CreatedAtMixin,Base):
    __tablename__='prestige_purchases'
    account_id: Mapped[UUID]
    request_key: Mapped[UUID]
    ruleset_id: Mapped[str]=mapped_column(ForeignKey('prestige_configurations.ruleset_id'))
    code: Mapped[str]=mapped_column(ForeignKey('prestige_upgrade_types.code'))
    from_level: Mapped[int]=mapped_column(Integer)
    to_level: Mapped[int]=mapped_column(Integer)
    snapshot: Mapped[dict]=mapped_column(JSONB)
    status: Mapped[str]=mapped_column(String(16))
    payment_message_id: Mapped[UUID]=mapped_column(ForeignKey('outbox_messages.id'),unique=True)
    receipt: Mapped[dict|None]=mapped_column(JSONB(none_as_null=True))
    result: Mapped[dict|None]=mapped_column(JSONB(none_as_null=True))
    completed_at: Mapped[datetime|None]=mapped_column(DateTime(timezone=True))
    __table_args__=(UniqueConstraint('account_id','request_key'),
        CheckConstraint('from_level >= 0 AND to_level = from_level + 1 AND to_level <= 5',name='level_range'),
        CheckConstraint("status IN ('PENDING','COMPLETED','REJECTED')",name='status'),
        Index('prestige_pending_account_idx','account_id',unique=True,postgresql_where=text("status = 'PENDING'")))
