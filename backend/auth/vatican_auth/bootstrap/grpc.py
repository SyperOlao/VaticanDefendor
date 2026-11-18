from dataclasses import dataclass, field
import os
import re


@dataclass(frozen=True)
class AuthGrpcSettings:
    enabled: bool = False
    bind: str = '0.0.0.0:50051'
    service_secret: str = field(default='', repr=False)
    certificate_file: str = ''
    private_key_file: str = field(default='', repr=False)

    def __post_init__(self):
        bind = (re.fullmatch(r'(?:[A-Za-z0-9_.-]+|\[[0-9A-Fa-f:]+\]):([0-9]{1,5})', self.bind)
                if isinstance(self.bind, str) else None)
        if type(self.enabled) is not bool or bind is None or not 0 <= int(bind[1]) <= 65535:
            raise ValueError('Invalid auth gRPC bind address')
        if self.enabled and (not isinstance(self.service_secret, str)
                or not 32 <= len(self.service_secret) <= 512 or not self.service_secret.isascii()
                or not self.service_secret.isprintable() or any(c.isspace() for c in self.service_secret)):
            raise ValueError('Auth gRPC requires VD_SERVICE_SECRET: 32..512 visible ASCII characters without whitespace')
        if bool(self.certificate_file) != bool(self.private_key_file):
            raise ValueError('Auth gRPC certificate and private key must be configured together')

    @classmethod
    def from_environment(cls):
        enabled = os.getenv('VD_AUTH_GRPC_ENABLED', 'false').lower()
        if enabled not in {'true', 'false'}:
            raise ValueError('VD_AUTH_GRPC_ENABLED must be true or false')
        return cls(enabled == 'true', os.getenv('VD_AUTH_GRPC_BIND', '0.0.0.0:50051'),
                   os.getenv('VD_SERVICE_SECRET', ''), os.getenv('VD_AUTH_GRPC_CERT_FILE', ''),
                   os.getenv('VD_AUTH_GRPC_KEY_FILE', ''))
