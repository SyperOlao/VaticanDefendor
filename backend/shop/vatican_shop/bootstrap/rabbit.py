from dataclasses import dataclass, field
import os
import re
from urllib.parse import quote, urlsplit


@dataclass(frozen=True)
class RabbitSettings:
    url: str = field(repr=False)
    queue_prefix: str = "vatican.inventory"
    exchange_prefix: str = "vatican"

    def __post_init__(self):
        if not isinstance(self.url, str):
            raise ValueError("Invalid shop RabbitMQ URL")
        try:
            parsed = urlsplit(self.url)
            valid = parsed.scheme in ("amqp", "amqps") and bool(parsed.hostname) and not parsed.fragment
            if parsed.port is not None:
                valid = valid and 1 <= parsed.port <= 65535
        except (ValueError, TypeError):
            valid = False
        if not valid:
            raise ValueError("Invalid shop RabbitMQ URL")
        if any(not isinstance(value, str) or re.fullmatch(r"[A-Za-z0-9_.-]{1,100}", value) is None
               for value in (self.queue_prefix, self.exchange_prefix)):
            raise ValueError("Invalid shop RabbitMQ resource prefix")

    @property
    def commands_exchange(self):
        return self.exchange_prefix + ".commands"

    @property
    def commands_queue(self):
        return self.queue_prefix + ".commands"


def environment_rabbit_url():
    explicit = os.getenv("VD_SHOP_RABBITMQ_URL")
    if explicit:
        return explicit
    user, password = os.environ["VD_RABBITMQ_USER"], os.environ["VD_RABBITMQ_PASSWORD"]
    host, port = os.environ["VD_RABBITMQ_HOST"], int(os.getenv("VD_RABBITMQ_PORT", "5672"))
    vhost = os.getenv("VD_RABBITMQ_VHOST", "vatican")
    return f"amqp://{quote(user, safe='')}:{quote(password, safe='')}@{host}:{port}/{quote(vhost, safe='')}"
