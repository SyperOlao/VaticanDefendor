"""Bounded cursors are positions, never authorization credentials."""
import base64
from dataclasses import dataclass
from datetime import datetime
import json
from uuid import UUID


@dataclass(frozen=True)
class PageCursor:
    scope: list
    position: list[str]
    revision: int | None = None

    def encode(self):
        value = {"v": 1, "scope": self.scope, "position": self.position, "revision": self.revision}
        return base64.urlsafe_b64encode(json.dumps(value, separators=(",", ":"), ensure_ascii=False).encode()).decode().rstrip("=")

    @classmethod
    def decode(cls, value, scope):
        try:
            if not isinstance(value, str) or not 1 <= len(value) <= 2048 or not value.isascii():
                raise ValueError()
            raw = base64.b64decode(value + "=" * (-len(value) % 4), altchars=b"-_", validate=True)
            data = json.loads(raw)
            if (not isinstance(data, dict) or set(data) != {"v", "scope", "position", "revision"}
                    or type(data["v"]) is not int or data["v"] != 1 or data["scope"] != scope
                    or not isinstance(data["position"], list)
                    or not all(isinstance(part, str) for part in data["position"])
                    or data["revision"] is not None and (type(data["revision"]) is not int or not 1 <= data["revision"] <= 9_223_372_036_854_775_807)):
                raise ValueError()
            return cls(scope, data["position"], data["revision"])
        except (ValueError, TypeError, KeyError, UnicodeError, RecursionError):
            raise ValueError("Invalid page cursor") from None

    def dated_position(self):
        if len(self.position) != 2:
            raise ValueError("Invalid dated cursor")
        date, identity = datetime.fromisoformat(self.position[0]), UUID(self.position[1])
        if date.tzinfo is None or date.utcoffset() is None:
            raise ValueError("Cursor date must have a timezone")
        return date, identity
