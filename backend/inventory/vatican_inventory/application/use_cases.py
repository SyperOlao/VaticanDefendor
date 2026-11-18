from uuid import UUID
import hashlib
import json
from .ports import UnitOfWorkFactory, DefinitionsGateway
from .errors import ApplicationError
from .catalog import EventName, MessageStatus, Operation
from .level_rewards import LevelRewardConsumer
from .cases import CaseFundingConsumer
from .run_rewards import RunRewardConsumer
from .items import ConsumeItems
from .validation import identifier, revision
from ..domain.items import item_id as validate_item_id
from ..domain.weapons import STARTER_CONFIGURATION, MAX_LOADOUT_SIZE, configuration_id as validate_configuration_id, definition_map


class InventoryUseCases:
    def __init__(self, uow: UnitOfWorkFactory, definitions: DefinitionsGateway):
        self.uow, self.definitions = uow, definitions
        self.case_funding = CaseFundingConsumer(uow)
        self.level_rewards = LevelRewardConsumer(uow, definitions)
        self.run_rewards = RunRewardConsumer(uow, definitions)
        self.item_consumption = ConsumeItems(uow)

    def _definitions(self):
        try:
            definitions = definition_map(self.definitions.get())
            if STARTER_CONFIGURATION not in definitions:
                raise ValueError()
            return definitions
        except (ValueError, TypeError, KeyError):
            raise ApplicationError(503, "Game content is invalid") from None

    def _read(self, repo, account_id, definitions, selected_only=False):
        weapons = repo.selected(account_id) if selected_only else repo.owned(account_id)
        loadout = repo.loadout(account_id)
        selected = set(loadout)
        visible = [row for row in weapons if not selected_only or row.instance_id in selected]
        if any(row.configuration_id not in definitions for row in visible):
            raise ApplicationError(503, "Weapon definition is unavailable")
        return {"Configurations": list(definitions.values()),
                "Weapons": [{"InstanceId": str(row.instance_id), "ConfigurationId": row.configuration_id,
                             "Level": row.level, "Experience": row.experience}
                            for row in visible],
                "Loadout": [str(i) for i in loadout], "Revision": repo.state(account_id).revision}

    def get(self, account_id, selected_only=False):
        identifier(account_id)
        definitions = self._definitions()
        with self.uow() as work:
            repo = work.repository
            repo.lock_player(account_id)
            repo.ensure(account_id)
            return self._read(repo, account_id, definitions, selected_only)

    def set_loadout(self, account_id, ids, expected_revision=None, request_key=None):
        identifier(account_id)
        if (not isinstance(ids, list) or not 1 <= len(ids) <= MAX_LOADOUT_SIZE
                or any(not isinstance(value, UUID) for value in ids) or len(set(ids)) != len(ids)):
            raise ApplicationError(422, "Select one to three distinct weapons")
        revision(expected_revision)
        if request_key is not None:
            identifier(request_key)
        digest = hashlib.sha256(json.dumps([[str(value) for value in ids], expected_revision]).encode()).digest()
        if request_key is not None:
            with self.uow() as work:
                repo = work.repository
                repo.lock_player(account_id)
                prior = repo.command(account_id, Operation.LOADOUT, request_key, digest)
                if prior is not None:
                    return prior["result"]
        definitions = self._definitions()
        with self.uow() as work:
            repo = work.repository
            repo.lock_player(account_id)
            if request_key is not None:
                prior = repo.command(account_id, Operation.LOADOUT, request_key, digest)
                if prior is not None:
                    return prior["result"]
            repo.ensure(account_id)
            if expected_revision is not None and repo.state(account_id).revision != expected_revision:
                raise ApplicationError(409, "Inventory revision changed")
            if any(repo.weapon(account_id, identity) is None for identity in ids):
                raise ApplicationError(403, "Weapon is not owned")
            if repo.loadout(account_id) != ids:
                repo.replace_loadout(account_id, ids)
            result = self._read(repo, account_id, definitions)
            if request_key is not None:
                confirmation = {"requestKey": str(request_key), "status": MessageStatus.APPLIED,
                                "InstanceIds": [str(value) for value in ids], "inventoryRevision": result["Revision"]}
                result["loadoutOperation"] = confirmation
                repo.record_command(account_id, Operation.LOADOUT, request_key, digest, {**confirmation, "result": result})
            return result

    def loadout_result(self, account_id, request_key):
        identifier(account_id)
        identifier(request_key)
        with self.uow() as work:
            repo = work.repository
            repo.lock_player(account_id)
            receipt = repo.command(account_id, Operation.LOADOUT, request_key)
            if receipt is None:
                raise ApplicationError(404, "Loadout operation not found")
            return {key: value for key, value in receipt.items() if key != "result"}

    def receive(self, message_id, event_name, schema_version, payload):
        identifier(message_id)
        if event_name == EventName.PURCHASE_GRANT:
            try:
                if (type(schema_version) is not int or schema_version != 1 or not isinstance(payload, dict)
                        or set(payload) != {"accountId", "operationId", "configurationId"}):
                    raise ValueError()
                account_id, operation_id = UUID(payload["accountId"]), UUID(payload["operationId"])
                digest = hashlib.sha256(json.dumps([event_name, schema_version, payload], sort_keys=True,
                    separators=(",", ":"), allow_nan=False).encode()).digest()
            except (ValueError, TypeError, KeyError, AttributeError):
                raise ApplicationError(422, "Invalid purchase grant message") from None
            receipt = self.grant(account_id, operation_id, payload["configurationId"],
                                 message_id=message_id, message_digest=digest)
            return {"messageId": str(message_id), "status": MessageStatus.APPLIED, "receipt": receipt}
        if event_name == EventName.ITEM_CONSUMPTION:
            return self.item_consumption.receive(message_id, event_name, schema_version, payload)
        if event_name == EventName.CASE_FUNDING:
            return self.case_funding.receive(message_id,event_name,schema_version,payload)
        if event_name in (EventName.LEVEL_REWARD,EventName.CASE_REWARD):
            return self.level_rewards.receive(message_id, event_name, schema_version, payload)
        return self.run_rewards.receive(message_id, event_name, schema_version, payload)

    @staticmethod
    def _purchase_prior(repo, account_id, operation_id, configuration_id, message_id, digest):
        if message_id is not None:
            repo.lock_message(message_id)
        repo.lock_player(account_id)
        consumed = message_id is not None and repo.received(message_id, digest)
        prior = repo.purchase_receipt(account_id, operation_id, configuration_id)
        if consumed and prior is None:
            raise ApplicationError(409, "Purchase message has no source receipt")
        if prior is not None and message_id is not None and not consumed:
            repo.record_message(message_id, digest)
        return prior

    def grant(self, account_id, operation_id, configuration_id, *, message_id=None, message_digest=None):
        identifier(account_id)
        identifier(operation_id)
        try:
            validate_configuration_id(configuration_id)
        except ValueError:
            raise ApplicationError(422, "Invalid weapon configuration") from None
        # Replays must not depend on current content availability or its later removal.
        with self.uow() as work:
            repo = work.repository
            prior = self._purchase_prior(repo, account_id, operation_id, configuration_id, message_id, message_digest)
            if prior is not None:
                return prior
        definitions = self._definitions()
        with self.uow() as work:
            repo = work.repository
            prior = self._purchase_prior(repo, account_id, operation_id, configuration_id, message_id, message_digest)
            if prior is not None:
                return prior
            receipt = {"accountId": str(account_id), "operationId": str(operation_id),
                       "configurationId": configuration_id}
            if configuration_id not in definitions:
                receipt.update(status=MessageStatus.REJECTED, code="UnknownWeaponConfiguration")
            else:
                repo.ensure(account_id)
                instance_id, _ = repo.grant(account_id, "purchase:" + str(operation_id), configuration_id)
                receipt.update(status=MessageStatus.APPLIED, instanceId=str(instance_id),
                               inventoryRevision=repo.state(account_id).revision)
            repo.record_purchase_receipt(account_id, operation_id, configuration_id, receipt)
            if message_id is not None:
                repo.record_message(message_id, message_digest)
            return receipt

    def grant_result(self, account_id, operation_id):
        identifier(account_id)
        identifier(operation_id)
        with self.uow() as work:
            repo = work.repository
            repo.lock_player(account_id)
            receipt = repo.purchase_result(account_id, operation_id)
            if receipt is None:
                raise ApplicationError(404, "Purchase grant not found")
            return receipt


    def items(self, account_id):
        identifier(account_id)
        with self.uow() as work:
            repo = work.repository
            repo.lock_player(account_id)
            repo.ensure(account_id)
            return {'items': [{'itemId': row.item_id, 'quantity': row.quantity} for row in repo.items(account_id)],
                    'revision': repo.state(account_id).revision}

    def item(self, account_id, item_id):
        identifier(account_id)
        try:
            validate_item_id(item_id)
        except ValueError:
            raise ApplicationError(422, "Invalid item identifier") from None
        with self.uow() as work:
            repo = work.repository
            repo.lock_player(account_id)
            item = repo.item(account_id, item_id)
            if item is None:
                raise ApplicationError(404, "Item not found")
            return {"itemId": item.item_id, "quantity": item.quantity, "revision": repo.state(account_id).revision}

    def weapon(self, account_id, instance_id):
        identifier(account_id)
        identifier(instance_id)
        # Ownership is checked before contacting content; unknown and foreign IDs both return 404.
        with self.uow() as work:
            repo = work.repository
            repo.lock_player(account_id)
            if repo.weapon(account_id, instance_id) is None:
                raise ApplicationError(404, "Weapon not found")
        definitions = self._definitions()
        with self.uow() as work:
            repo = work.repository
            repo.lock_player(account_id)
            weapon = repo.weapon(account_id, instance_id)
            if weapon is None:
                raise ApplicationError(404, "Weapon not found")
            if weapon.configuration_id not in definitions:
                raise ApplicationError(503, "Weapon definition is unavailable")
            return {"Weapon": {"InstanceId": str(weapon.instance_id), "ConfigurationId": weapon.configuration_id,
                               "Level": weapon.level, "Experience": weapon.experience},
                    "Configuration": definitions[weapon.configuration_id], "Revision": repo.state(account_id).revision}
