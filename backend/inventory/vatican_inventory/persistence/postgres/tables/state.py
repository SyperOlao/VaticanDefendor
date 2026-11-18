from uuid import UUID
from sqlalchemy import BigInteger, CheckConstraint
from sqlalchemy.orm import Mapped, mapped_column
from ..base import Base


class InventoryState(Base):
    __tablename__ = "inventory_states"
    account_id: Mapped[UUID] = mapped_column(primary_key=True)
    revision: Mapped[int] = mapped_column(BigInteger, server_default="1")
    __table_args__ = (CheckConstraint("revision > 0", name="revision_positive"),)
