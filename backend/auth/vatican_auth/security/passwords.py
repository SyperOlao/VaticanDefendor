import asyncio
import secrets

from argon2 import PasswordHasher
from argon2.exceptions import InvalidHashError, VerificationError

from ..application.models import PasswordCheck
from ..application.policy import AuthPolicy


class Argon2Passwords:
    def __init__(self, policy: AuthPolicy):
        self.hasher = PasswordHasher(time_cost=3, memory_cost=65536, parallelism=1)
        self._capacity = asyncio.Semaphore(policy.password_concurrency)
        self._dummy_hash: str | None = None

    async def initialize(self) -> None:
        self._dummy_hash = await self.hash(secrets.token_urlsafe(32))

    async def _run(self, operation, *args):
        async with self._capacity:
            task = asyncio.create_task(asyncio.to_thread(operation, *args))
            try:
                return await asyncio.shield(task)
            except asyncio.CancelledError:
                await task
                raise

    async def hash(self, password: str) -> str:
        return await self._run(self.hasher.hash, password)

    async def verify(self, password: str, encoded_hash: str | None) -> PasswordCheck:
        if self._dummy_hash is None:
            raise RuntimeError("Password service must be initialized")
        try:
            await self._run(self.hasher.verify, encoded_hash or self._dummy_hash, password)
        except (VerificationError, InvalidHashError):
            return PasswordCheck(False)
        if encoded_hash is None:
            return PasswordCheck(False)
        replacement = await self.hash(password) if self.hasher.check_needs_rehash(encoded_hash) else None
        return PasswordCheck(True, replacement)
