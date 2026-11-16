import hashlib
import re
import secrets

from ..application.errors import InvalidCredentials


class SecureTokens:
    @staticmethod
    def generate() -> str:
        return secrets.token_urlsafe(32)

    @staticmethod
    def digest(token: str) -> bytes:
        if not isinstance(token, str) or re.fullmatch(r"[A-Za-z0-9_-]{43}", token) is None:
            raise InvalidCredentials("Invalid or expired credential")
        return hashlib.sha256(token.encode("ascii")).digest()

    @staticmethod
    def key_hash(key: str) -> bytes:
        return hashlib.sha256(key.encode("utf-8")).digest()
