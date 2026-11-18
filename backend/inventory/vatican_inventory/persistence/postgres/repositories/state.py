import hashlib
from uuid import uuid4, uuid5
from sqlalchemy import delete, func, select, tuple_
from sqlalchemy.dialects.postgresql import insert
from .messaging import MessagingRepository
from ..tables import IdempotencyRecord, InventoryItem, InventoryState, OwnedWeapon, WeaponLoadout, MockPaymentReceipt
from ....application.errors import ApplicationError
from ....application.catalog import MessageStatus, Operation, OperationStatus
from ....domain.weapons import InventoryState as State, Item, Weapon, WeaponEntry, STARTER_CONFIGURATION


class StateRepository(MessagingRepository):
    def mock_payment_receipt(self, transaction, line, unit, digest):
        self.session.execute(select(func.pg_advisory_xact_lock(func.hashtextextended(
            f'inventory.mock.payment:{transaction}:{line}:{unit}',0))))
        row = self.session.get(MockPaymentReceipt,(transaction,line,unit))
        if row is None:return None
        if row.payload_hash != digest:
            raise ApplicationError(409,'MOCK payment source payload changed')
        return dict(row.receipt_json)

    def record_mock_payment(self, transaction, line, unit, account, digest, receipt):
        self.session.add(MockPaymentReceipt(transaction_id=transaction,line_index=line,unit_index=unit,
            account_id=account,payload_hash=digest,receipt_json=receipt))

    def command(self, account_id, operation, request_key, digest=None):
        row = self.session.get(IdempotencyRecord, (account_id, operation, str(request_key)))
        if row is None:
            return None
        if digest is not None and row.payload_hash != digest:
            raise ApplicationError(409, "Operation payload changed")
        if row.status not in (OperationStatus.COMPLETED, OperationStatus.REJECTED) or row.result_json is None:
            raise ApplicationError(409, "Operation has no final result")
        return dict(row.result_json)

    def record_command(self, account_id, operation, request_key, digest, receipt):
        self.session.add(IdempotencyRecord(account_id=account_id, operation=operation,
            request_key=str(request_key), payload_hash=digest,
            status=OperationStatus.COMPLETED if receipt["status"] == MessageStatus.APPLIED else OperationStatus.REJECTED,
            result_json=receipt, completed_at=func.now()))

    def purchase_receipt(self, account_id, operation_id, configuration_id):
        prior = self.purchase_result(account_id, operation_id)
        if prior is not None and prior["configurationId"] != configuration_id:
            raise ApplicationError(409, "Purchase configuration changed")
        if prior is not None:
            self.command(account_id, Operation.PURCHASE, operation_id, hashlib.sha256(configuration_id.encode()).digest())
        return prior

    def purchase_result(self, account_id, operation_id):
        receipt = self.command(account_id, Operation.PURCHASE, operation_id)
        if receipt is not None:
            if receipt["status"] == MessageStatus.APPLIED and "inventoryRevision" not in receipt:
                receipt["inventoryRevision"] = self.state(account_id).revision
                row = self.session.get(IdempotencyRecord, (account_id, Operation.PURCHASE, str(operation_id)))
                row.result_json = receipt
            return receipt
        # Purchases created before command receipts were introduced are authoritative too.
        prior = self.session.scalar(select(OwnedWeapon).where(
            OwnedWeapon.account_id == account_id, OwnedWeapon.grant_key == "purchase:" + str(operation_id)))
        if prior is None:
            return None
        receipt = {"accountId": str(account_id), "operationId": str(operation_id),
                   "configurationId": prior.configuration_id, "instanceId": str(prior.instance_id),
                   "inventoryRevision": self.state(account_id).revision, "status": MessageStatus.APPLIED}
        self.record_purchase_receipt(account_id, operation_id, prior.configuration_id, receipt)
        return receipt

    def record_purchase_receipt(self, account_id, operation_id, configuration_id, receipt):
        self.record_command(account_id, Operation.PURCHASE, operation_id,
                            hashlib.sha256(configuration_id.encode()).digest(), receipt)

    def ensure(self, account_id):
        new_state = self.session.scalar(insert(InventoryState).values(account_id=account_id)
            .on_conflict_do_nothing().returning(InventoryState.account_id))
        instance_id = uuid5(account_id, "weapon.starter.sword")
        created = self.session.scalar(insert(OwnedWeapon).values(instance_id=instance_id,
            account_id=account_id, configuration_id=STARTER_CONFIGURATION, grant_key="starter")
            .on_conflict_do_nothing(index_elements=[OwnedWeapon.account_id, OwnedWeapon.grant_key])
            .returning(OwnedWeapon.instance_id))
        equipped = False
        if not self.loadout(account_id):
            # Imported starters can have a non-deterministic historical instance_id.
            starter_id = created or self.session.scalar(select(OwnedWeapon.instance_id).where(
                OwnedWeapon.account_id == account_id, OwnedWeapon.grant_key == "starter"))
            self.session.add(WeaponLoadout(account_id=account_id, slot=0, instance_id=starter_id))
            equipped = True
        if created or equipped:
            if new_state is None:
                self.session.get(InventoryState, account_id).revision += 1
            self.session.flush()

    def state(self, account_id):
        row = self.session.get(InventoryState, account_id)
        if row is None:
            raise ApplicationError(503, "Inventory state is unavailable")
        return State(row.revision)

    def owned(self, account_id):
        return [Weapon(row.instance_id, row.configuration_id, row.level, row.experience)
                for row in self.session.scalars(select(OwnedWeapon).where(OwnedWeapon.account_id == account_id)
                    .order_by(OwnedWeapon.created_at, OwnedWeapon.instance_id))]

    def weapon(self, account_id, instance_id):
        row = self.session.scalar(select(OwnedWeapon).where(
            OwnedWeapon.account_id == account_id, OwnedWeapon.instance_id == instance_id))
        return Weapon(row.instance_id, row.configuration_id, row.level, row.experience) if row else None

    def selected(self, account_id):
        rows = self.session.scalars(select(OwnedWeapon).join(WeaponLoadout,
            (WeaponLoadout.instance_id == OwnedWeapon.instance_id) & (WeaponLoadout.account_id == OwnedWeapon.account_id))
            .where(WeaponLoadout.account_id == account_id).order_by(WeaponLoadout.slot))
        return [Weapon(row.instance_id, row.configuration_id, row.level, row.experience) for row in rows]

    def weapon_page(self, account_id, limit, after, configuration_id):
        query = select(OwnedWeapon).where(OwnedWeapon.account_id == account_id)
        if after is not None:
            query = query.where(tuple_(OwnedWeapon.created_at, OwnedWeapon.instance_id) > after)
        if configuration_id is not None:
            query = query.where(OwnedWeapon.configuration_id == configuration_id)
        rows = self.session.scalars(query.order_by(OwnedWeapon.created_at, OwnedWeapon.instance_id).limit(limit))
        return [WeaponEntry(Weapon(row.instance_id, row.configuration_id, row.level, row.experience), row.created_at) for row in rows]

    def item_page(self, account_id, limit, after, kind):
        query = select(InventoryItem).where(InventoryItem.account_id == account_id)
        if after is not None:
            query = query.where(InventoryItem.item_id > after)
        if kind is not None:
            query = query.where(InventoryItem.item_id.startswith("case:" if kind == "CASE" else "item:"))
        return [Item(row.item_id, row.quantity) for row in self.session.scalars(query.order_by(InventoryItem.item_id).limit(limit))]

    def loadout(self, account_id):
        return list(self.session.scalars(select(WeaponLoadout.instance_id).where(WeaponLoadout.account_id == account_id)
                    .order_by(WeaponLoadout.slot)))

    def replace_loadout(self, account_id, ids):
        self.session.execute(delete(WeaponLoadout).where(WeaponLoadout.account_id == account_id))
        self.session.add_all(WeaponLoadout(account_id=account_id, slot=slot, instance_id=i) for slot, i in enumerate(ids))
        self.session.get(InventoryState, account_id).revision += 1
        self.session.flush()

    def grant(self, account_id, source_key, configuration_id):
        prior = self.session.scalar(select(OwnedWeapon).where(OwnedWeapon.account_id == account_id,
                                                             OwnedWeapon.grant_key == source_key))
        if prior is not None:
            if prior.configuration_id != configuration_id:
                raise ApplicationError(409, "Grant configuration changed")
            return prior.instance_id, False
        instance_id = uuid4()
        self.session.add(OwnedWeapon(instance_id=instance_id, account_id=account_id,
            configuration_id=configuration_id, grant_key=source_key))
        self.session.get(InventoryState, account_id).revision += 1
        return instance_id, True

    def set_progress(self, instance_id, level, experience):
        row = self.session.get(OwnedWeapon, instance_id)
        row.level, row.experience = level, experience

    def items(self, account_id):
        return [Item(row.item_id, row.quantity) for row in self.session.scalars(select(InventoryItem)
                .where(InventoryItem.account_id == account_id).order_by(InventoryItem.item_id))]

    def item(self, account_id, item_id):
        row = self.session.get(InventoryItem, (account_id, item_id))
        return Item(row.item_id, row.quantity) if row else None


    def quantity(self, account_id, item_id):
        row = self.session.get(InventoryItem, (account_id, item_id))
        return row.quantity if row else 0

    def set_quantity(self, account_id, item_id, quantity):
        row = self.session.get(InventoryItem, (account_id, item_id))
        if row:
            row.quantity = quantity
        else:
            self.session.add(InventoryItem(account_id=account_id, item_id=item_id, quantity=quantity))
        self.session.get(InventoryState, account_id).revision += 1
