from uuid import UUID
from sqlalchemy import BigInteger, CheckConstraint, ForeignKey, ForeignKeyConstraint, Index, Integer, String, UniqueConstraint, text
from sqlalchemy.dialects.postgresql import JSONB
from sqlalchemy.orm import Mapped, mapped_column
from ..base import Base, CreatedAtMixin


class InventoryItem(Base):
    __tablename__ = "inventory"
    account_id: Mapped[UUID] = mapped_column(primary_key=True)
    item_id: Mapped[str] = mapped_column(String(160), primary_key=True)
    quantity: Mapped[int] = mapped_column(BigInteger)
    __table_args__ = (CheckConstraint("quantity >= 0", name="quantity_nonnegative"),)


class OwnedWeapon(CreatedAtMixin, Base):
    __tablename__ = "owned_weapons"
    instance_id: Mapped[UUID] = mapped_column(primary_key=True)
    account_id: Mapped[UUID] = mapped_column()
    configuration_id: Mapped[str] = mapped_column(String(80))
    level: Mapped[int] = mapped_column(Integer, server_default="1")
    experience: Mapped[int] = mapped_column(Integer, server_default="0")
    grant_key: Mapped[str] = mapped_column(String(80))
    __table_args__ = (
        UniqueConstraint("instance_id", "account_id"),
        CheckConstraint("level >= 1", name="level_positive"),
        CheckConstraint("experience >= 0", name="experience_nonnegative"),
        UniqueConstraint("account_id", "grant_key", name="owned_weapons_account_id_grant_key_key"),
        Index("owned_weapons_account_idx", "account_id"),
        Index("ix_inventory_weapon_page", "account_id", "created_at", "instance_id"),
    )


class WeaponLoadout(Base):
    __tablename__ = "weapon_loadouts"
    account_id: Mapped[UUID] = mapped_column(primary_key=True)
    slot: Mapped[int] = mapped_column(Integer, primary_key=True)
    instance_id: Mapped[UUID] = mapped_column(ForeignKey("owned_weapons.instance_id"))
    __table_args__ = (CheckConstraint("slot BETWEEN 0 AND 2", name="slot_range"),
                      ForeignKeyConstraint(["instance_id", "account_id"], ["owned_weapons.instance_id", "owned_weapons.account_id"], name="fk_weapon_loadout_owner"),
                      UniqueConstraint("account_id", "instance_id", name="weapon_loadouts_account_id_instance_id_key"))
