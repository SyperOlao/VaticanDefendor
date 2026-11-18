class AuthError(Exception):
    code = "AuthError"
    public_detail = "The operation could not be completed"
    field: str | None = None


class InvalidInput(AuthError):
    code = "InvalidInput"
    public_detail = "Request parameters are invalid"


class InvalidLogin(InvalidInput):
    code = "InvalidLogin"
    public_detail = "Login must contain 3–64 ASCII letters, digits, dots, underscores or hyphens"
    field = "login"


class PasswordPolicyViolation(InvalidInput):
    code = "PasswordPolicyViolation"
    field = "password"

    def __init__(self, minimum: int, maximum: int):
        self.public_detail = f"Password must contain {minimum}–{maximum} characters"
        super().__init__("Password length policy was not met")


class InvalidCredentials(AuthError):
    code = "InvalidCredentials"
    public_detail = "Invalid or expired credential"


class LoginUnavailable(AuthError):
    code = "LoginUnavailable"
    public_detail = "Login is unavailable"


class AccountAlreadyRegistered(AuthError):
    code = "AccountAlreadyRegistered"
    public_detail = "Account already has a login"


class RateLimitExceeded(AuthError):
    code = "RateLimitExceeded"
    public_detail = "Too many authentication attempts; retry later"

    def __init__(self, retry_after: int):
        self.retry_after = max(1, int(retry_after))
        super().__init__("Too many authentication attempts")


class PermissionDenied(AuthError):
    code = "PermissionDenied"
    public_detail = "Required permission or role is missing"


class DelegationDenied(PermissionDenied):
    code = "DelegationDenied"
    public_detail = "Cannot manage permissions you do not hold"


class ResourceNotFound(AuthError):
    code = "ResourceNotFound"
    public_detail = "Requested resource was not found"


class ResourceConflict(AuthError):
    code = "ResourceConflict"
    public_detail = "Operation conflicts with the current state"


class LastAdministrator(ResourceConflict):
    code = "LastAdministrator"
    public_detail = "The minimum number of active role assignments must be preserved"

    def __init__(self, role_code: str):
        self.public_detail = f"The minimum number of active {role_code} assignments must be preserved"
        super().__init__("Minimum active role assignments would be violated")


class ProtectedRole(ResourceConflict):
    code = "ProtectedRole"
    public_detail = "Built-in role definition cannot be changed"

    def __init__(self, role_code: str):
        self.public_detail = f"Built-in {role_code} definition cannot be changed"
        super().__init__("Protected role definition cannot be changed")


class RoleNotAssignable(ResourceConflict):
    code = "RoleNotAssignable"
    public_detail = "Role is not assignable"


class RegisteredAccountRequired(InvalidInput):
    code = "RegisteredAccountRequired"
    public_detail = "Roles can only be assigned to registered accounts"
