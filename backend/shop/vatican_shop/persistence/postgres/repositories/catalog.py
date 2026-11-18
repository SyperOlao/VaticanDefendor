from datetime import datetime, timezone

from sqlalchemy import func, select

from ..tables import ShopCatalogAudit, ShopCatalogState, ShopOffer
from ....application.catalog import CatalogAction
from ....application.models import OfferRecord


class CatalogRepository:
    def __init__(self, session):
        self.session = session

    @staticmethod
    def _record(row):
        content = {"id": row.id, "weaponConfigurationId": row.configuration_id, "title": row.title,
                   "price": row.price, "purchaseLimit": row.purchase_limit, "currencyId": "prestige",
                   "available": row.available, "unavailableReason": row.unavailable_reason, "imageKey": row.image_key}
        return OfferRecord(content, row.revision, row.created_at, row.updated_at, row.deleted_at)

    def revision(self, write=False):
        row = self.session.scalar(select(ShopCatalogState).where(ShopCatalogState.id == 1)
                                  .with_for_update(read=not write).execution_options(populate_existing=True))
        if row is None:
            raise RuntimeError("Shop catalog state is missing")
        return row.revision

    def snapshot(self):
        revision = self.revision()
        rows = self.session.scalars(select(ShopOffer).where(ShopOffer.deleted_at.is_(None)).order_by(ShopOffer.id))
        return revision, [self._record(row).content for row in rows]

    def get(self, offer_id):
        row = self.session.get(ShopOffer, offer_id, populate_existing=True)
        return self._record(row) if row is not None else None

    def list(self, limit, after, include_deleted):
        query = select(ShopOffer)
        if not include_deleted:
            query = query.where(ShopOffer.deleted_at.is_(None))
        if after is not None:
            query = query.where(ShopOffer.id > after)
        return [self._record(row) for row in self.session.scalars(query.order_by(ShopOffer.id).limit(limit))]

    def count(self):
        return self.session.scalar(select(func.count()).select_from(ShopOffer).where(ShopOffer.deleted_at.is_(None)))

    def _audit(self, actor_id, row, action, before):
        self.session.flush()
        after = self._record(row)
        self.session.add(ShopCatalogAudit(actor_account_id=actor_id, offer_id=row.id,
            action=action, before_json=before.response() if before else None, after_json=after.response()))
        state = self.session.get(ShopCatalogState, 1)
        state.revision += 1
        return after

    @staticmethod
    def _assign(row, content):
        row.configuration_id = content["weaponConfigurationId"]
        row.title = content["title"]
        row.image_key = content.get("imageKey")
        row.price = content["price"]
        row.purchase_limit = content["purchaseLimit"]
        row.available = content["available"]
        row.unavailable_reason = content["unavailableReason"]

    def create(self, content, actor_id):
        row = ShopOffer(id=content["id"])
        self._assign(row, content)
        self.session.add(row)
        return self._audit(actor_id, row, CatalogAction.CREATE, None)

    def update(self, content, actor_id):
        row = self.session.get(ShopOffer, content["id"])
        before = self._record(row)
        self._assign(row, content)
        row.revision += 1
        row.updated_at = datetime.now(timezone.utc)
        return self._audit(actor_id, row, CatalogAction.UPDATE, before)

    def delete(self, offer_id, actor_id):
        row = self.session.get(ShopOffer, offer_id)
        before = self._record(row)
        row.available = False
        row.deleted_at = row.updated_at = datetime.now(timezone.utc)
        row.revision += 1
        self._audit(actor_id, row, CatalogAction.DELETE, before)
