from .ports import AuthGateway, Principal, UnitOfWorkFactory
from .errors import ApplicationError


class AuthenticatePlayer:
    def __init__(self, auth: AuthGateway, uow: UnitOfWorkFactory):
        self.auth, self.uow = auth, uow

    def execute(self, authorization: str | None) -> Principal:
        principal = self.identify(authorization)
        self.ensure_profile(principal)
        return principal

    def identify(self, authorization: str | None) -> Principal:
        scheme, _, token = (authorization or "").partition(" ")
        if scheme.lower() != "bearer" or not token or len(token) > 512 or any(c.isspace() for c in token):
            raise ApplicationError(401, "Bearer credential required")
        # Auth resolves the owner UUID before a game transaction is opened.
        return self.auth.authenticate(token)

    def ensure_profile(self, principal: Principal) -> None:
        with self.uow() as work:
            work.profiles.ensure(principal.account_id)
