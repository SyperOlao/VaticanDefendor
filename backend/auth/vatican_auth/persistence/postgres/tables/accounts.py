from sqlalchemy import String
from sqlalchemy.orm import Mapped, mapped_column

from ....application.catalog import ACCOUNT_STATUS_MAP
from ..base import Base, CreatedAtMixin, IdentityMixin


class Account(IdentityMixin, CreatedAtMixin, Base):
    __tablename__ = "accounts"
    account_status: Mapped[str] = mapped_column(String(32), server_default=ACCOUNT_STATUS_MAP["active"])
