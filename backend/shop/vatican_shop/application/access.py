from .catalog import ROLE_MAP
from .errors import ApplicationError


def administrator(auth, token):
    actor = auth.authenticate(token)
    if actor.is_guest or ROLE_MAP["admin"] not in actor.roles:
        raise ApplicationError(403, "Administrator role required")
    return actor.account_id
