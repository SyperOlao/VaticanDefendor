from sqlalchemy import func, select

from ..tables import PurchaseOperation


class ShopRepository:
    def __init__(self, session):
        self.session = session

    def operation(self, account_id, operation_id):
        return self.session.get(PurchaseOperation, (account_id, operation_id))

    def purchased_counts(self, account_id):
        return dict(self.session.execute(select(PurchaseOperation.offer_id, func.count()).where(
            PurchaseOperation.account_id == account_id,
            PurchaseOperation.receipt_json["status"].astext == "Completed"
        ).group_by(PurchaseOperation.offer_id)).all())

    def record(self, **values):
        self.session.add(PurchaseOperation(**values))
