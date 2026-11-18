from .ports import AuthGateway, Principal, UnitOfWorkFactory
from .errors import ApplicationError


class AuthenticatePlayer:
    def __init__(self, auth: AuthGateway, uow: UnitOfWorkFactory):
        self.auth, self.uow = auth, uow

    def execute(self, authorization: str | None) -> Principal:
        scheme, _, token = (authorization or "").partition(" ")
        if scheme.lower() != "bearer" or not token or len(token) > 512 or any(c.isspace() for c in token):
            raise ApplicationError(401, "Bearer credential required")
        # Auth resolves the owner UUID before a game transaction is opened.
        principal = self.auth.authenticate(token)
        with self.uow() as work:
            work.profiles.ensure(principal.account_id)
        return principal
