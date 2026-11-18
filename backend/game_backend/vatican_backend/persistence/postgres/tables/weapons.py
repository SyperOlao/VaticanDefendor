from uuid import UUID

from sqlalchemy import CheckConstraint, ForeignKey, ForeignKeyConstraint, Index, Integer, String, UniqueConstraint, text
from sqlalchemy.dialects.postgresql import JSONB
from sqlalchemy.orm import Mapped, mapped_column

from ..base import Base, CreatedAtMixin










class WeaponConfiguration(Base):
    __tablename__ = "weapon_configurations"
    configuration_id: Mapped[str] = mapped_column(String(80), primary_key=True)
    configuration_json: Mapped[dict] = mapped_column(JSONB)
