from collections.abc import Iterator

from dishka import Provider, Scope, from_context, make_container, provide

from .di import ServiceProvider
from .rabbit import RabbitSettings
from .settings import Settings
from ..application.broker import AcceptBrokerDelivery, ProcessBrokerCommands, PublishBrokerEvents
from ..application.broker_ports import BrokerPublisher
from ..integrations.rabbit import RabbitBroker


class BrokerProvider(Provider):
    rabbit_settings = from_context(provides=RabbitSettings, scope=Scope.APP)

    @provide(scope=Scope.APP)
    def broker(self, settings: RabbitSettings) -> Iterator[RabbitBroker]:
        broker = RabbitBroker(settings)
        try:
            yield broker
        finally:
            broker.close()

    @provide(scope=Scope.APP)
    def publisher(self, broker: RabbitBroker) -> BrokerPublisher:
        return broker

    receive = provide(AcceptBrokerDelivery, scope=Scope.APP)
    process = provide(ProcessBrokerCommands, scope=Scope.APP)
    publish = provide(PublishBrokerEvents, scope=Scope.APP)


def create_broker_container(settings, rabbit, *providers):
    return make_container(ServiceProvider(), BrokerProvider(), *providers,
                          context={Settings: settings, RabbitSettings: rabbit})
