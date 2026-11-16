from ipaddress import ip_address
import re

from .errors import InvalidInput, InvalidLogin, PasswordPolicyViolation
from .policy import AuthPolicy


def normalize_login(login: str) -> str:
    if not isinstance(login, str) or re.fullmatch(r"[A-Za-z0-9_.-]{3,64}", login) is None:
        raise InvalidLogin()
    return login.lower()


def validate_password(password: str, policy: AuthPolicy, *, creating: bool = True) -> None:
    minimum = policy.password_min_length if creating else 1
    if not isinstance(password, str) or not minimum <= len(password) <= policy.password_max_length:
        raise PasswordPolicyViolation(minimum, policy.password_max_length)


def normalize_ip(value: str) -> str:
    try:
        return str(ip_address(value))
    except ValueError:
        raise InvalidInput("Client IP must be a valid IP address") from None
