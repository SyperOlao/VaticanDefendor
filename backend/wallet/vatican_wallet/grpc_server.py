"""Entry point for internal wallet reads; commands continue through RabbitMQ."""
import os
import signal
from threading import Event

from .bootstrap.di import create_container
from .bootstrap.settings import Settings
from .presentation.grpc import create_server


def main():
    stopped = Event()
    for name in (signal.SIGTERM, signal.SIGINT):
        signal.signal(name, lambda *_: stopped.set())
    settings = Settings.from_environment()
    container = create_container(settings)
    server = None
    try:
        server, _ = create_server(container, settings, os.getenv('VD_WALLET_GRPC_BIND', '0.0.0.0:50052'),
            certificate=os.getenv('VD_WALLET_GRPC_CERT_FILE'), private_key=os.getenv('VD_WALLET_GRPC_KEY_FILE'))
        server.start()
        stopped.wait()
    finally:
        if server is not None:
            server.stop(10).wait()
        container.close()


if __name__ == '__main__':
    main()
