"""Validate the explicit transport choice; RPC failures never trigger HTTP fallback."""
import re


def validate_auth_settings(settings):
    if settings.auth_transport not in ('http', 'grpc'):
        raise ValueError('VD_AUTH_TRANSPORT must be http or grpc')
    if settings.auth_transport == 'grpc':
        target = re.fullmatch(r'(?:[A-Za-z0-9_.-]+|\[[0-9A-Fa-f:]+\]):([0-9]{1,5})', settings.auth_grpc_target)
        if target is None or not 1 <= int(target[1]) <= 65535:
            raise ValueError('VD_AUTH_GRPC_TARGET must be host:port')
        secret = settings.service_secret
        if (not 32 <= len(secret) <= 512 or not secret.isascii() or not secret.isprintable()
                or any(c.isspace() for c in secret)):
            raise ValueError('gRPC requires a valid VD_SERVICE_SECRET')
    if type(settings.auth_grpc_timeout_ms) is not int or not 50 <= settings.auth_grpc_timeout_ms <= 30000:
        raise ValueError('VD_AUTH_GRPC_TIMEOUT_MS must be 50..30000')
    if type(settings.auth_grpc_attempts) is not int or not 1 <= settings.auth_grpc_attempts <= 5:
        raise ValueError('VD_AUTH_GRPC_ATTEMPTS must be 1..5')
