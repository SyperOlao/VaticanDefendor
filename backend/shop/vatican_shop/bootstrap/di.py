from collections.abc import Iterator
import httpx
from dishka import Provider, Scope, from_context, make_container, provide
from .settings import Settings
from .rabbit import RabbitSettings
from ..application.catalog import InventoryTransport
from ..persistence.postgres.database import Database
from ..persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from ..application.ports import (AuthGateway, ImageStorage, UnitOfWorkFactory, DefinitionsGateway, WeaponDefinitionsGateway,
                                 PurchaseGateway as PurchaseGatewayPort)
from ..application.content import AdminCatalog, ReadCatalog
from ..application.media import AdminMedia
from ..application.operations import AdminPurchases
from ..integrations.s3 import S3ImageStorage
from ..application.use_cases import ShopUseCases
from ..application.health import HealthUseCases
from ..application.analytics import DeliverAnalytics
from ..application.ports import AnalyticsGateway
from ..integrations.analytics import HttpAnalyticsGateway
from ..migrate import SCHEMA_REVISION
from ..integrations.http import AuthHttpGateway, DefinitionsHttpGateway
from ..integrations.auth_grpc import AuthGrpcGateway
from ..integrations.shop import PurchaseGateway
from ..integrations.rabbit import RabbitInventoryPublisher
from ..integrations.rabbit_purchase import BrokerPurchaseGateway
from ..application.use_cases import SettlePurchases
from ..application.mock_payments import MockPayments, VerifyMockPayments, FulfillMockPayments
from ..application.mock_ports import RewardBundlesGateway, MockProviderGateway, MockEntitlementGateway
from ..domain.mock_payments import MockPolicy
from ..integrations.mock_payments import RewardBundlesHttpGateway, LocalMockProvider, MockEntitlementsHttpGateway


class ServiceProvider(Provider):
    settings = from_context(provides=Settings, scope=Scope.APP)

    @provide(scope=Scope.APP)
    def mock_policy(self,settings: Settings) -> MockPolicy:
        return MockPolicy(settings.mock_payments_enabled,settings.app_environment,settings.mock_control_secret,
                          settings.mock_webhook_secret,settings.mock_receipt_secret,settings.mock_grant_secret)

    @provide(scope=Scope.APP)
    def reward_bundles(self,client: httpx.Client,settings: Settings) -> RewardBundlesGateway:
        return RewardBundlesHttpGateway(client,settings)

    @provide(scope=Scope.APP)
    def mock_provider(self,uow: UnitOfWorkFactory,policy: MockPolicy) -> MockProviderGateway:
        return LocalMockProvider(uow,policy)

    @provide(scope=Scope.APP)
    def mock_entitlements(self,client: httpx.Client,settings: Settings) -> MockEntitlementGateway:
        return MockEntitlementsHttpGateway(client,settings)

    @provide(scope=Scope.APP)
    def client(self) -> Iterator[httpx.Client]:
        with httpx.Client(timeout=httpx.Timeout(5, connect=2), trust_env=False, follow_redirects=False) as client:
            yield client

    @provide(scope=Scope.APP)
    def database(self, settings: Settings) -> Iterator[Database]:
        database = Database(settings.database_url)
        try:
            yield database
        finally:
            database.close()

    @provide(scope=Scope.APP)
    def uow(self, database: Database) -> UnitOfWorkFactory:
        return PostgresUnitOfWorkFactory(database)

    @provide(scope=Scope.APP)
    def auth(self, client: httpx.Client, settings: Settings) -> Iterator[AuthGateway]:
        if settings.auth_transport == 'grpc':
            gateway = AuthGrpcGateway(settings)
            try:
                yield gateway
            finally:
                gateway.close()
        else:
            yield AuthHttpGateway(client, settings.auth_url)

    @provide(scope=Scope.APP)
    def health(self, uow: UnitOfWorkFactory) -> HealthUseCases:
        return HealthUseCases(uow, SCHEMA_REVISION)

    @provide(scope=Scope.APP)
    def definitions(self, client: httpx.Client, settings: Settings) -> WeaponDefinitionsGateway:
        return DefinitionsHttpGateway(client, settings.game_url)

    @provide(scope=Scope.APP)
    def images(self, settings: Settings) -> ImageStorage:
        return S3ImageStorage(settings)

    @provide(scope=Scope.APP)
    def catalog(self, uow: UnitOfWorkFactory, definitions: WeaponDefinitionsGateway, images: ImageStorage) -> DefinitionsGateway:
        return ReadCatalog(uow, definitions, images)

    @provide(scope=Scope.APP)
    def admin(self, uow: UnitOfWorkFactory, auth: AuthGateway, definitions: WeaponDefinitionsGateway, images: ImageStorage) -> AdminCatalog:
        return AdminCatalog(uow, auth, definitions, images)

    @provide(scope=Scope.APP)
    def purchase_gateway(self, client: httpx.Client, settings: Settings) -> Iterator[PurchaseGatewayPort]:
        from contextlib import ExitStack
        with ExitStack() as resources:
            gateway = PurchaseGateway(client, settings)
            if settings.inventory_transport == InventoryTransport.RABBITMQ:
                publisher = RabbitInventoryPublisher(RabbitSettings(settings.rabbitmq_url,
                    settings.rabbit_queue_prefix, settings.rabbit_exchange_prefix))
                reader = None
                if settings.inventory_read_transport == 'grpc':
                    from ..integrations.inventory_grpc import InventoryGrpcClient
                    reader = InventoryGrpcClient(settings.inventory_grpc_target, settings.service_secret, settings.inventory_grpc_ca_file)
                    resources.callback(reader.close)
                gateway = BrokerPurchaseGateway(client, settings, publisher, reader)
            if settings.wallet_transport == 'rabbitmq':
                from .wallet_rabbit import RabbitSettings as WalletRabbitSettings
                from ..integrations.wallet_grpc import WalletGrpcClient
                from ..integrations.wallet_publisher import RabbitWalletPublisher
                from ..integrations.wallet_purchase import WalletBrokerPurchaseGateway
                reader = WalletGrpcClient(settings.wallet_grpc_target, settings.service_secret, settings.wallet_grpc_ca_file)
                resources.callback(reader.close)
                gateway = WalletBrokerPurchaseGateway(RabbitWalletPublisher(WalletRabbitSettings.from_environment()), reader, gateway)
            yield gateway

    @provide(scope=Scope.APP)
    def analytics_gateway(self,client: httpx.Client,settings: Settings) -> AnalyticsGateway:
        return HttpAnalyticsGateway(client,settings.analytics_url,settings.service_secret)

    analytics = provide(DeliverAnalytics,scope=Scope.APP)
    purchases = provide(SettlePurchases, scope=Scope.APP)
    use_case = provide(ShopUseCases, scope=Scope.APP)
    media = provide(AdminMedia, scope=Scope.APP)
    operations = provide(AdminPurchases, scope=Scope.APP)
    mock_payments = provide(MockPayments, scope=Scope.APP)
    mock_verifier = provide(VerifyMockPayments, scope=Scope.APP)
    mock_fulfiller = provide(FulfillMockPayments, scope=Scope.APP)


def create_container(settings, *providers):
    return make_container(ServiceProvider(), *providers, context={Settings: settings})
