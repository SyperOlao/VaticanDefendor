import base64
import binascii
from datetime import datetime, timezone
import json
from uuid import UUID


def encode_cursor(created_at, account_id, operation_id):
    value = [created_at.isoformat(), str(account_id), str(operation_id)]
    return base64.urlsafe_b64encode(json.dumps(value, separators=(",", ":")).encode()).decode().rstrip("=")


def decode_cursor(value):
    if value is None:
        return None
    try:
        if not isinstance(value, str) or not 1 <= len(value) <= 256:
            raise ValueError()
        decoded = json.loads(base64.b64decode(value + "=" * (-len(value) % 4), altchars=b"-_", validate=True))
        if not isinstance(decoded, list) or len(decoded) != 3 or any(not isinstance(item, str) for item in decoded):
            raise ValueError()
        created_at = datetime.fromisoformat(decoded[0])
        if created_at.tzinfo is None:
            raise ValueError()
        return created_at.astimezone(timezone.utc), UUID(decoded[1]), UUID(decoded[2])
    except (ValueError, TypeError, binascii.Error, UnicodeError, OverflowError):
        raise ValueError("Invalid purchase cursor") from None
