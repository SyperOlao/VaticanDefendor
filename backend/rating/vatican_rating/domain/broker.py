"""Transport-neutral command envelopes and durable delivery states."""
from dataclasses import dataclass
from enum import StrEnum
import hashlib
import json
from uuid import UUID, NAMESPACE_URL, uuid5


MAX_BODY_BYTES = 16_384
MAX_ATTEMPTS = 20
LEASE_SECONDS = 60


class InboxStatus(StrEnum):
    PENDING = "PENDING"
    APPLIED = "APPLIED"
    REJECTED = "REJECTED"
    REVIEW = "REVIEW"


class Destination(StrEnum):
    RESULT = "rabbit.results"
    DEAD = "rabbit.dead"
    QUARANTINE = "rabbit.quarantine"


class BrokerCode(StrEnum):
    INVALID_ENVELOPE = "InvalidEnvelope"
    MESSAGE_ID_MISMATCH = "MessageIdMismatch"
    MESSAGE_CONFLICT = "MessageConflict"
    TOO_LARGE = "PayloadTooLarge"
    COMMAND_REJECTED = "CommandRejected"
    COMMAND_CONFLICT = "CommandConflict"
    HANDLER_UNAVAILABLE = "HandlerUnavailable"
    HANDLER_FAILED = "HandlerFailed"
    PUBLICATION_UNAVAILABLE = "PublicationUnavailable"


class InvalidEnvelope(ValueError):
    def __init__(self, code=BrokerCode.INVALID_ENVELOPE):
        self.code = code


def _unique_object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError()
        value[key] = item
    return value


def _validate_strings(value):
    if isinstance(value, str):
        if "\x00" in value:
            raise ValueError()
        value.encode("utf-8")  # Reject unpaired surrogates that PostgreSQL JSONB cannot store.
    elif isinstance(value, dict):
        for key, item in value.items():
            _validate_strings(key)
            _validate_strings(item)
    elif isinstance(value, list):
        for item in value:
            _validate_strings(item)


@dataclass(frozen=True)
class Command:
    message_id: UUID
    envelope: dict
    digest: bytes

    @classmethod
    def parse(cls, body: bytes, property_id=None, content_type=None, content_encoding=None):
        if len(body) > MAX_BODY_BYTES:
            raise InvalidEnvelope(BrokerCode.TOO_LARGE)
        try:
            if content_type not in (None, "application/json") or content_encoding not in (None, "utf-8"):
                raise ValueError()
            value = json.loads(body.decode("utf-8"), object_pairs_hook=_unique_object,
                               parse_constant=lambda _: (_ for _ in ()).throw(ValueError()))
            if (not isinstance(value, dict) or set(value) != {"messageId", "eventName", "schemaVersion", "payload"}
                    or not isinstance(value["messageId"], str) or not isinstance(value["eventName"], str)
                    or not 1 <= len(value["eventName"]) <= 128
                    or type(value["schemaVersion"]) is not int or not 1 <= value["schemaVersion"] <= 2_147_483_647
                    or not isinstance(value["payload"], dict)):
                raise ValueError()
            identity = UUID(value["messageId"])
            _validate_strings(value)
            if property_id is not None and (not isinstance(property_id, str) or UUID(property_id) != identity):
                raise InvalidEnvelope(BrokerCode.MESSAGE_ID_MISMATCH)
            value["messageId"] = str(identity)
            canonical = json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()
            return cls(identity, value, hashlib.sha256(canonical).digest())
        except InvalidEnvelope:
            raise
        except (ValueError, TypeError, KeyError, UnicodeError, RecursionError, OverflowError):
            raise InvalidEnvelope() from None


@dataclass(frozen=True)
class CommandJob:
    message_id: UUID
    envelope: dict
    lease: UUID
    attempts: int
    generation: int


@dataclass(frozen=True)
class EventJob:
    id: UUID
    destination: str
    event_name: str
    schema_version: int
    aggregate_id: UUID
    payload: dict
    lease: UUID
    attempts: int


def rejection_id(body, property_id, content_type, content_encoding, code):
    metadata = json.dumps([property_id, content_type, content_encoding, code], default=str, ensure_ascii=True).encode()
    digest = hashlib.sha256(metadata)
    digest.update(body)
    return uuid5(NAMESPACE_URL, "vatican.rating.rejection:" + digest.hexdigest())


def result_id(message_id, generation, destination):
    return uuid5(message_id, f"rating.broker:{destination}:{generation}")


def retry_seconds(attempts):
    return min(300, 2 ** min(attempts, 9))
