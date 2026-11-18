from collections.abc import Iterator
from contextlib import ExitStack
from dataclasses import dataclass
import httpx
from dishka import Provider, Scope, make_container, provide, from_context
from ..application.access import AuthenticatePlayer
from ..application.energy import EnergyService, EnergyUseCases, ConfigureEnergy
from ..application.progression import ConfigureProgression, ProgressionUseCases
from ..application.level_rewards import LevelRewardsUseCases, PublishRewardBundle
from ..application.prestige import PrestigeUseCases,PublishPrestige
from ..application.cases import CaseUseCases, PublishCase
from ..domain.clock import Clock, UtcClock
from ..application.content import ContentUseCases
from ..application.profiles import ProfileUseCases, HealthUseCases
from ..application.runs import RunUseCases
from ..application.weapons import WeaponDefinitionsUseCases
from ..application.messaging import DeliverRewards
from ..application.ports import AuthGateway, InventoryGateway, InventoryCommandReader, RewardDeliveryGateway, UnitOfWorkFactory
from ..integrations.http import AuthHttpGateway, InventoryHttpGateway, HttpRewardDelivery
from ..persistence.postgres.database import Database
from ..persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from ..migrate import SCHEMA_REVISION
from .settings import Settings
from .weapon_catalog import LoadCatalog
from .avatar_catalog import load_avatars
from ..application.local_runs import LocalRunUseCases,ConfigureLocalRuns
from ..application.rating_broker import AcceptRatingResult


@dataclass(frozen=True)
class DeliveryResources:
    client: httpx.Client
    override: RewardDeliveryGateway | None


@dataclass(frozen=True)
class InventoryOverride:
    gateway: InventoryGateway | None


@dataclass(frozen=True)
class AuthOverride:
    gateway: AuthGateway | None


class GameProvider(Provider):
    settings = from_context(provides=Settings, scope=Scope.APP)
    auth_override = from_context(provides=AuthOverride, scope=Scope.APP)

    @provide(scope=Scope.APP)
    def auth(self, settings: Settings, override: AuthOverride, resources: DeliveryResources) -> Iterator[AuthGateway]:
        if override.gateway is not None:
            yield override.gateway
        elif settings.auth_transport == 'grpc':
            from ..integrations.auth_grpc import AuthGrpcGateway
            gateway = AuthGrpcGateway(settings)
            try:
                yield gateway
            finally:
                gateway.close()
        else:
            yield AuthHttpGateway(resources.client, settings.auth_url)

    inventory_override = from_context(provides=InventoryOverride, scope=Scope.APP)
    delivery_resources = from_context(provides=DeliveryResources, scope=Scope.APP)

    @provide(scope=Scope.APP)
    def delivery(self, settings: Settings, resources: DeliveryResources, reader: InventoryCommandReader) -> Iterator[RewardDeliveryGateway]:
        if resources.override is not None:
            yield resources.override
            return
        with ExitStack() as resources_stack:
            http = HttpRewardDelivery(resources.client, {'wallet': settings.wallet_url, 'inventory': settings.inventory_url,
                'rating': settings.rating_url, 'analytics': settings.analytics_url}, settings.service_secret)
            gateway = http
            if settings.inventory_transport == 'rabbitmq':
                from .inventory_rabbit import RabbitSettings
                from ..integrations.inventory_publisher import RabbitInventoryPublisher
                from ..integrations.inventory_rabbit import InventoryRabbitDelivery
                publisher = RabbitInventoryPublisher(RabbitSettings(settings.inventory_rabbitmq_url,
                    settings.inventory_rabbit_queue_prefix, settings.inventory_rabbit_exchange_prefix))
                gateway = InventoryRabbitDelivery(publisher, reader, gateway)
            if settings.wallet_transport == 'rabbitmq':
                from .wallet_rabbit import RabbitSettings as WalletRabbitSettings
                from ..integrations.wallet_publisher import RabbitWalletPublisher
                from ..integrations.wallet_grpc import WalletGrpcClient
                from ..integrations.wallet_rabbit import WalletRabbitDelivery
                wallet_reader = WalletGrpcClient(settings.wallet_grpc_target, settings.service_secret, settings.wallet_grpc_ca_file)
                resources_stack.callback(wallet_reader.close)
                gateway = WalletRabbitDelivery(RabbitWalletPublisher(WalletRabbitSettings.from_environment()), wallet_reader, gateway)
            if settings.rating_transport == 'rabbitmq':
                from .rating_rabbit import RabbitSettings
                from ..integrations.rating_rabbit import RatingRabbitBroker, RatingRabbitDelivery
                broker = RatingRabbitBroker(RabbitSettings.from_environment())
                resources_stack.callback(broker.close)
                gateway = RatingRabbitDelivery(broker, gateway)
            yield gateway

    @provide(scope=Scope.APP)
    def inventory_reads(self, settings: Settings, resources: DeliveryResources) -> Iterator[InventoryCommandReader]:
        if settings.inventory_read_transport == 'grpc':
            from ..integrations.inventory_grpc import InventoryGrpcClient
            reader = InventoryGrpcClient(settings.inventory_grpc_target, settings.service_secret, settings.inventory_grpc_ca_file)
            try:yield reader
            finally:reader.close()
        else:
            yield InventoryHttpGateway(resources.client, settings.inventory_url, settings.service_secret)

    @provide(scope=Scope.APP)
    def inventory(self, reader: InventoryCommandReader, override: InventoryOverride) -> InventoryGateway:
        return override.gateway or reader

    @provide(scope=Scope.APP)
    def database(self, settings: Settings) -> Iterator[Database]:
        database = Database(settings.database_url, settings.pool_size)
        try:
            yield database
        finally:
            database.close()

    @provide(scope=Scope.APP)
    def uow(self, database: Database) -> UnitOfWorkFactory:
        return PostgresUnitOfWorkFactory(database)

    @provide(scope=Scope.APP)
    def content(self, uow: UnitOfWorkFactory, settings: Settings) -> ContentUseCases:
        return ContentUseCases(uow, settings.content_admin_secret)

    @provide(scope=Scope.APP)
    def weapons(self, uow: UnitOfWorkFactory) -> WeaponDefinitionsUseCases:
        return WeaponDefinitionsUseCases(uow, LoadCatalog())

    @provide(scope=Scope.APP)
    def health(self, uow: UnitOfWorkFactory) -> HealthUseCases:
        return HealthUseCases(uow, SCHEMA_REVISION)

    clock = provide(UtcClock, provides=Clock, scope=Scope.APP)

    @provide(scope=Scope.APP)
    def energy_service(self, settings: Settings) -> EnergyService:
        return EnergyService(settings.energy_ruleset_id)

    @provide(scope=Scope.APP)
    def energy(self, uow: UnitOfWorkFactory, service: EnergyService, clock: Clock) -> EnergyUseCases:
        return EnergyUseCases(uow, service, clock)

    @provide(scope=Scope.APP)
    def level_rewards(self, uow: UnitOfWorkFactory, service: EnergyService, clock: Clock) -> LevelRewardsUseCases:
        return LevelRewardsUseCases(uow, service, clock)

    publish_reward_bundle = provide(PublishRewardBundle, scope=Scope.APP)
    configure_energy = provide(ConfigureEnergy, scope=Scope.APP)
    configure_progression = provide(ConfigureProgression, scope=Scope.APP)
    configure_local_runs = provide(ConfigureLocalRuns,scope=Scope.APP)

    @provide(scope=Scope.APP)
    def local_runs(self,uow: UnitOfWorkFactory,settings: Settings,inventory: InventoryGateway,clock: Clock) -> LocalRunUseCases:
        return LocalRunUseCases(uow,settings,inventory,clock)
    progression = provide(ProgressionUseCases, scope=Scope.APP)
    access = provide(AuthenticatePlayer, scope=Scope.APP)
    rating_results = provide(AcceptRatingResult, scope=Scope.APP)
    @provide(scope=Scope.APP)
    def profiles(self,uow: UnitOfWorkFactory,settings: Settings) -> ProfileUseCases:
        return ProfileUseCases(uow,load_avatars(settings.avatar_catalog_file))

    @provide(scope=Scope.APP)
    def runs(self, uow: UnitOfWorkFactory, settings: Settings, inventory: InventoryGateway, clock: Clock) -> RunUseCases:
        return RunUseCases(uow, settings, inventory, clock=clock)

    @provide(scope=Scope.APP)
    def cases(self, uow: UnitOfWorkFactory, service: EnergyService, clock: Clock) -> CaseUseCases:
        return CaseUseCases(uow,service,clock)

    publish_prestige = provide(PublishPrestige,scope=Scope.APP)

    @provide(scope=Scope.APP)
    def prestige(self,uow: UnitOfWorkFactory,settings: Settings,clock: Clock) -> PrestigeUseCases:
        return PrestigeUseCases(uow,settings,clock)

    publish_case = provide(PublishCase, scope=Scope.APP)

    @provide(scope=Scope.APP)
    def rewards(self, uow: UnitOfWorkFactory, gateway: RewardDeliveryGateway, cases: CaseUseCases, prestige: PrestigeUseCases) -> DeliverRewards:
        return DeliverRewards(uow,gateway,cases,prestige)


def create_game_container(settings, auth=None, inventory=None, delivery=None):
    client = httpx.Client(timeout=httpx.Timeout(5, connect=2), follow_redirects=False, trust_env=False)
    context = {Settings: settings,
        AuthOverride: AuthOverride(auth),
        InventoryOverride: InventoryOverride(inventory),
        DeliveryResources: DeliveryResources(client, delivery)}
    return make_container(GameProvider(), context=context), client


@dataclass(frozen=True)
class GameUseCases:
    profiles: ProfileUseCases
    runs: RunUseCases
    weapons: WeaponDefinitionsUseCases
    health: HealthUseCases
    rewards: DeliverRewards
    energy: EnergyUseCases
    progression: ProgressionUseCases
    level_rewards: LevelRewardsUseCases
    cases: CaseUseCases
    local_runs: LocalRunUseCases
    prestige: PrestigeUseCases
