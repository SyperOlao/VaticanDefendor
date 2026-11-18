from .errors import ApplicationError
from .ports import AuthGateway


class AuthenticatePlayer:
    def __init__(self, auth: AuthGateway):
        self.auth = auth

    def execute(self, authorization):
        scheme, _, token = (authorization or "").partition(" ")
        if (scheme.lower() != "bearer" or not token or not token.isascii() or not token.isprintable()
                or len(token) > 512 or any(c.isspace() for c in token)):
            raise ApplicationError(401, "Bearer credential required")
        return self.auth.authenticate(token)
