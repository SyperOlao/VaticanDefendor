from dataclasses import dataclass, field
import os
import re
from urllib.parse import quote, urlsplit


@dataclass(frozen=True)
class RabbitSettings:
    url: str = field(repr=False)
    queue_prefix: str = "vatican.wallet"
    exchange_prefix: str = "vatican"

    def __post_init__(self):
        try:
            value = urlsplit(self.url)
            if value.scheme not in ('amqp', 'amqps') or not value.hostname or value.fragment:
                raise ValueError()
            if value.port is not None and not 1 <= value.port <= 65535:
                raise ValueError()
        except (ValueError, TypeError, AttributeError):
            raise ValueError("RabbitMQ requires a valid AMQP URL") from None
        if any(not isinstance(value, str) or re.fullmatch(r"[A-Za-z0-9_.-]{1,100}", value) is None
               for value in (self.queue_prefix, self.exchange_prefix)):
            raise ValueError("Invalid RabbitMQ resource prefix")

    @property
    def commands_exchange(self):
        return self.exchange_prefix + ".commands"

    @property
    def dead_exchange(self):
        return self.exchange_prefix + ".dead"

    @property
    def commands_queue(self):
        return self.queue_prefix + ".commands"

    @property
    def dead_queue(self):
        return self.queue_prefix + ".dead"

    @classmethod
    def from_environment(cls):
        url = os.getenv("VD_RATING_RABBITMQ_URL")
        if not url:
            user, password = os.environ["VD_RABBITMQ_USER"], os.environ["VD_RABBITMQ_PASSWORD"]
            host, port = os.environ["VD_RABBITMQ_HOST"], int(os.getenv("VD_RABBITMQ_PORT", "5672"))
            vhost = os.getenv("VD_RABBITMQ_VHOST", "vatican")
            url = f"amqp://{quote(user, safe='')}:{quote(password, safe='')}@{host}:{port}/{quote(vhost, safe='')}"
        return cls(url,
                   os.getenv("VD_RATING_RABBIT_QUEUE_PREFIX", "vatican.wallet"),
                   os.getenv("VD_RATING_RABBIT_EXCHANGE_PREFIX", "vatican"))
