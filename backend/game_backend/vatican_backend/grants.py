import base64
import hashlib
import hmac
import struct
from datetime import datetime, timezone
from uuid import UUID

from .settings import ServerSlot


def issue_grant(account_id: UUID, run_id: UUID, slot: ServerSlot,
                generation: int, ttl_seconds: int) -> tuple[str, int]:
    expires_at = int(datetime.now(timezone.utc).timestamp()) + ttl_seconds
    payload = (b"\x01" + account_id.bytes_le + run_id.bytes_le + slot.server_id.bytes_le
               + struct.pack(">II", expires_at, generation))
    signature = hmac.new(slot.secret.encode("utf-8"), payload, hashlib.sha256).digest()
    token = base64.urlsafe_b64encode(payload + signature).rstrip(b"=").decode("ascii")
    if len(token) > 128:
        raise ValueError("Grant exceeds Netcode FixedString128Bytes")
    return token, expires_at
