"""Fictional examples shared by the OpenAPI schemas and operation descriptions."""
from ..migrate import SCHEMA_REVISION
import hashlib
import json

ACCOUNT = "9ef8ec7c-2120-4825-a0cf-05fc01f97a1d"
OPERATION = "3b321688-36fc-43f3-9819-21f08b994a92"
MESSAGE = "49ca66e1-3d56-469a-9714-72440b19e7c8"
WEAPON = "a96dc6c2-a7a6-4f9a-8334-ea16e7db2d12"
SOURCE = "b91c717b-631c-4f36-8cb7-dc0d06abeb9a"
EVENT = "80dc8329-5d36-4407-bf89-863b2f86a791"
REJECTION = "de088b37-f621-435c-979b-4bda83f24f92"
DATE = "2026-11-17T12:00:00Z"

DEFINITION = {"Id": "sword.common", "ExperiencePerLevel": 100, "MaximumLevel": 20}
OWNED_WEAPON = {"InstanceId": WEAPON, "ConfigurationId": "sword.common", "Level": 2, "Experience": 100}
WEAPONS = {"Configurations": [DEFINITION], "Weapons": [OWNED_WEAPON], "Loadout": [WEAPON], "Revision": 7}
OWNED_PAGE = {key: value for key, value in WEAPONS.items() if key != "Configurations"} | {"nextCursor": None}
LOADOUT = {"InstanceIds": [WEAPON], "expectedRevision": 7, "requestKey": OPERATION}
LOADOUT_RESULT = {"requestKey": OPERATION, "status": "APPLIED", "InstanceIds": [WEAPON], "inventoryRevision": 7}
ITEMS = {"items": [{"itemId": "case:daily", "quantity": 1}, {"itemId": "item:repair-kit", "quantity": 4}], "revision": 7}
ITEMS_PAGE = {"items": [{"itemId": "item:repair-kit", "quantity": 4}], "revision": 7, "nextCursor": None}
ITEM = {"itemId": "item:repair-kit", "quantity": 4, "revision": 7}
GRANT = {"accountId": ACCOUNT, "operationId": OPERATION, "configurationId": "sword.common"}
GRANTED = {**GRANT, "status": "APPLIED", "instanceId": WEAPON, "inventoryRevision": 8}
GRANT_REJECTED = {**GRANT, "configurationId": "missing.weapon", "status": "REJECTED", "code": "UnknownWeaponConfiguration"}
MOCK_GRANT = {'accountId': ACCOUNT, 'transactionId': SOURCE, 'lineIndex': 0, 'unitIndex': 0, 'configurationId': 'sword.common'}
MOCK_GRANTED = {**MOCK_GRANT, 'status': 'APPLIED', 'instanceId': WEAPON, 'inventoryRevision': 8}
CONSUMPTION = {"accountId": ACCOUNT, "operationId": OPERATION, "itemId": "item:repair-kit", "quantity": 2}
CONSUMED = {**CONSUMPTION, "status": "APPLIED", "quantityAfter": 2, "inventoryRevision": 8}
INSUFFICIENT_ITEMS = {**CONSUMPTION, "status": "REJECTED", "quantityAfter": 0, "code": "InsufficientItems"}


def example(summary, value, description=None):
    result = {"summary": summary, "value": value}
    if description:
        result["description"] = description
    return result


def command(name, payload):
    return {"messageId": MESSAGE, "eventName": name, "schemaVersion": 1, "payload": payload}


LEVEL_PAYLOAD = {"accountId": ACCOUNT, "claimId": SOURCE, "itemIndex": 0,
                 "rewardType": "ITEM", "rewardRef": "repair-kit", "quantity": 3}
CASE_PAYLOAD = {"accountId": ACCOUNT, "openingId": SOURCE, "itemIndex": 0,
                "rewardType": "WEAPON", "rewardRef": "sword.common", "quantity": 1}
CASE_CONSUMPTION = {"accountId": ACCOUNT, "openingId": SOURCE, "caseCode": "daily", "quantity": 1}
RUN_PAYLOAD = {"accountId": ACCOUNT, "runId": SOURCE, "configurationId": "sword.common", "snapshot": WEAPONS}
COMMAND_EXAMPLES = {
    "level.reward": example("Награда уровня: 3 ремонтных набора", command("level.reward", LEVEL_PAYLOAD)),
    "case.reward": example("Награда кейса: один экземпляр оружия", command("case.reward", CASE_PAYLOAD)),
    "case.consume": example("Списать один принадлежащий игроку кейс", command("case.consume", CASE_CONSUMPTION)),
    "inventory.item.consume": example("Списать 2 обычных предмета", command("inventory.item.consume", CONSUMPTION)),
    "inventory.weapon.grant": example("Выдать оружие по операции покупки", command("inventory.weapon.grant", GRANT)),
    "run.weapon_reward": example("Награда забега и XP закреплённого оружия", command("run.weapon_reward", RUN_PAYLOAD),
        "snapshot берётся из /internal/players/{account_id}/snapshot до начала забега и сохраняется игровым сервисом."),
}
MESSAGE_RESULT = {"messageId": MESSAGE, "status": "APPLIED", "receipt": {**LEVEL_PAYLOAD,
                  "itemId": "item:repair-kit", "quantityAfter": 3, "inventoryRevision": 8}}
MESSAGE_REJECTED_ITEMS = {"messageId": MESSAGE, "status": "APPLIED", "receipt": INSUFFICIENT_ITEMS}
MESSAGE_REJECTED_CASE = {"messageId": MESSAGE, "status": "APPLIED", "receipt": {**CASE_CONSUMPTION,
    "itemId": "case:daily", "quantityAfter": 0, "outcome": "REJECTED", "code": "InsufficientCases"}}

EVENT_SUMMARY = {"eventId": EVENT, "commandOrRejectionId": MESSAGE, "destination": "rabbit.results",
    "eventName": "inventory.command.result", "attempts": 1, "status": "PUBLISHED", "lastError": None,
    "createdAt": DATE, "availableAt": DATE, "leased": False}
FAILED_EVENT = {**EVENT_SUMMARY, "status": "REVIEW", "attempts": 20, "lastError": "PublicationUnavailable"}
COMMAND_SUMMARY = {"messageId": MESSAGE, "status": "APPLIED", "attempts": 1, "deliveryGeneration": 1,
    "lastError": None, "createdAt": DATE, "availableAt": DATE, "leased": False}
COMMAND_STATUS = {key: value for key, value in COMMAND_SUMMARY.items() if key not in ("createdAt", "availableAt", "leased")} | {
    "payloadSha256": hashlib.sha256(json.dumps(command('level.reward', LEVEL_PAYLOAD), sort_keys=True, separators=(',', ':')).encode()).hexdigest(),
    "result": MESSAGE_RESULT, "events": [{key: EVENT_SUMMARY[key] for key in ("eventId", "destination", "attempts", "status")}]}
REJECTION_SUMMARY = {"rejectionId": REJECTION, "messageId": None, "code": "InvalidEnvelope",
    "bodySha256": "e1f2a3b4" * 8, "bodySize": 5000, "bodyTruncated": True, "createdAt": DATE,
    "event": {**EVENT_SUMMARY, "commandOrRejectionId": REJECTION, "destination": "rabbit.quarantine",
              "eventName": "inventory.command.dead"}}
LIVE = {"status": "live"}
READY = {"status": "ready", "schemaRevision": SCHEMA_REVISION}
