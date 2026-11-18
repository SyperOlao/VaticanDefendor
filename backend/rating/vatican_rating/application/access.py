from .errors import ApplicationError
from .models import Principal
from .ports import AuthGateway


class AuthenticatePlayer:
    def __init__(self, auth: AuthGateway):
        self.auth = auth

    def execute(self, authorization: str | None) -> Principal:
        scheme, _, token = (authorization or "").partition(" ")
        if (scheme.lower() != "bearer" or not token or len(token) > 512 or not token.isascii()
                or not token.isprintable() or any(c.isspace() for c in token)):
            raise ApplicationError(401, "Bearer credential required")
        return self.auth.authenticate(token)
