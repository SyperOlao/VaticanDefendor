import asyncio
from time import monotonic

from starlette.datastructures import Headers
from starlette.exceptions import HTTPException
from starlette.types import ASGIApp, Message, Receive, Scope, Send

from .access import check_service_secret


MAX_MESSAGE_BYTES = 16_384
MESSAGE_READ_TIMEOUT_SECONDS = 5


class MessageIngressMiddleware:
    def __init__(self, app: ASGIApp, service_secret: str):
        self.app, self.service_secret = app, service_secret

    async def __call__(self, scope: Scope, receive: Receive, send: Send):
        if scope['type'] != 'http':
            await self.app(scope, receive, send)
            return
        path = scope['path']
        root_path = scope.get('root_path', '').rstrip('/')
        if root_path and path.startswith(root_path + '/'):
            path = path[len(root_path):]
        if scope['method'] != 'POST' or path != '/internal/messages':
            await self.app(scope, receive, send)
            return

        headers = Headers(scope=scope)
        check_service_secret(self.service_secret, headers.get('x-service-secret'))
        length = headers.get('content-length')
        if length is not None:
            if not length.isascii() or not length.isdecimal():
                raise HTTPException(400)
            if len(length) > 20 or int(length) > MAX_MESSAGE_BYTES:
                raise HTTPException(413)

        received = 0
        deadline = monotonic() + MESSAGE_READ_TIMEOUT_SECONDS

        async def bounded_receive() -> Message:
            nonlocal received
            remaining = deadline - monotonic()
            if remaining <= 0:
                raise HTTPException(408)
            try:
                async with asyncio.timeout(remaining):
                    message = await receive()
            except TimeoutError:
                raise HTTPException(408) from None
            if message['type'] == 'http.request':
                received += len(message.get('body', b''))
                if received > MAX_MESSAGE_BYTES:
                    raise HTTPException(413)
            return message

        await self.app(scope, bounded_receive, send)

