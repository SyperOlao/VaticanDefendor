from uuid import UUID
from typing import Annotated
from ..domain.progression import MAX_TARGETS,MAX_COUNT
from pydantic import BaseModel,ConfigDict,Field
from . import openapi_examples as ex


class StrictBody(BaseModel):
    model_config=ConfigDict(extra='forbid')


class ServerReady(StrictBody):
    """Возможности и версии доверенного dedicated-процесса."""
    model_config=ConfigDict(json_schema_extra={'examples':[ex.READY]})
    prestigeSchemaVersion: int = Field(default=0,strict=True,ge=0,le=1,description='1 — сборка применяет prestigeJson; 0 — старый сервер без поддержки бонусов.')
    contentReleaseId: str = Field(min_length=1,max_length=128,description='Опубликованный релиз, загруженный сервером.')
    protocolVersion: int = Field(ge=1,description='Версия сетевого JoinProtocol Unity, не версия HTTP API.')
    transport: str = Field(default='udp-loopback',description='Транспорт должен совпадать с настроенным слотом: udp-loopback или dtls.')
    rulesetId: str|None = Field(default=None,pattern=r'^[A-Za-z0-9_.-]{1,128}$',description='Правила XP; если поле не передано, используется contentReleaseId.')


class AssignmentAck(StrictBody):
    """Назначенный забег и актуальное поколение сервера."""
    model_config=ConfigDict(json_schema_extra={'examples':[ex.ACK]})
    runId: UUID = Field(description='Возьмите из ответа assignment/recover.')
    generation: int = Field(ge=1,description='Возьмите из того же назначения. После recovery старое поколение не принимается.')


class CheckpointPublish(StrictBody):
    """Checkpoint для восстановления доверенного dedicated-процесса."""
    model_config=ConfigDict(json_schema_extra={'examples':[ex.CHECKPOINT]})
    runId: UUID = Field(description='Текущий забег сервера.')
    generation: int = Field(ge=1,description='Поколение из назначения.')
    schemaVersion: int = Field(ge=4,le=6,description='Поддерживается checkpoint schemaVersion=4/5/6.')
    simulationTick: int = Field(ge=0,description='Монотонный номер тика симуляции.')
    snapshotJson: str = Field(min_length=2,max_length=8_000_000,description='Точная строка сериализованного RunCheckpointData. Пример сокращён: для реального recovery нужен полный экспорт Unity.')
    sha256: str = Field(pattern='^[0-9a-f]{64}$',description='SHA-256 точных UTF-8 байтов snapshotJson, lowercase hex.')


CounterTarget=Annotated[str,Field(pattern=r'^[A-Za-z0-9_.:-]{1,128}$')]
CounterValue=Annotated[int,Field(strict=True,ge=0,le=MAX_COUNT)]


class RunStatsRequest(StrictBody):
    """Счётчики по ключам целей; XP рассчитывает сервер по закреплённым правилам."""
    model_config=ConfigDict(json_schema_extra={'examples':[ex.STATS]})
    heroKills: dict[CounterTarget,CounterValue] = Field(default_factory=dict,max_length=MAX_TARGETS,description='Убийства героя: ключ типа врага → количество, например {"orc":2}.')
    towerKills: dict[CounterTarget,CounterValue] = Field(default_factory=dict,max_length=MAX_TARGETS,description='Убийства построек. Не включайте их второй раз в heroKills.')
    buildingUpgrades: dict[CounterTarget,CounterValue] = Field(default_factory=dict,max_length=MAX_TARGETS,description='Количество улучшений по типу здания, например {"church":4}. Не итоговый уровень.')


class MatchCounterRequest(StrictBody):
    key: CounterTarget
    count: CounterValue


class MatchResourcesRequest(StrictBody):
    wood: int = Field(strict=True, ge=0, le=9_223_372_036_854_775_807)
    stone: int = Field(strict=True, ge=0, le=9_223_372_036_854_775_807)
    iron: int = Field(strict=True, ge=0, le=9_223_372_036_854_775_807)


class MatchAggregateRequest(StrictBody):
    enemiesSpawned: int = Field(strict=True, ge=0, le=MAX_COUNT)
    enemiesKilled: int = Field(strict=True, ge=0, le=MAX_COUNT)
    otherKills: int = Field(strict=True, ge=0, le=MAX_COUNT)
    heroDamageMilliUnits: int = Field(strict=True, ge=0, le=9_223_372_036_854_775_807)
    towerDamageMilliUnits: int = Field(strict=True, ge=0, le=9_223_372_036_854_775_807)
    heroKills: list[MatchCounterRequest] = Field(max_length=MAX_TARGETS)
    towerKills: list[MatchCounterRequest] = Field(max_length=MAX_TARGETS)
    buildingsBuilt: list[MatchCounterRequest] = Field(max_length=MAX_TARGETS)
    buildingUpgrades: list[MatchCounterRequest] = Field(max_length=MAX_TARGETS)
    resourcesProduced: MatchResourcesRequest
    resourcesSpent: MatchResourcesRequest


class MatchWaveRequest(MatchAggregateRequest):
    waveNumber: int = Field(strict=True, ge=1, le=1000)


class MatchResultSnapshot(MatchAggregateRequest):
    schemaVersion: int = Field(strict=True, ge=1, le=1)
    complete: bool = Field(strict=True)
    durationMs: int = Field(strict=True, ge=0, le=9_223_372_036_854_775_807)
    simulationTick: int = Field(strict=True, ge=0, le=9_223_372_036_854_775_807)
    buildings: list[MatchCounterRequest] = Field(max_length=MAX_TARGETS)
    waves: list[MatchWaveRequest] = Field(max_length=1000)


class RunResult(StrictBody):
    """Итог dedicated-забега: отправляется только доверенным игровым сервером."""
    model_config=ConfigDict(json_schema_extra={'examples':[ex.RESULT]})
    schemaVersion: int = Field(default=1, strict=True, ge=1, le=2)
    serverId: UUID | None = None
    rulesetId: str | None = Field(default=None, max_length=128)
    statistics: MatchResultSnapshot | None = Field(default=None, description='MatchResultSnapshot schemaVersion=1; required for result v2. Validated against wave and run aggregates.')
    runId: UUID = Field(description='Завершаемый забег из назначения.')
    accountId: UUID = Field(description='Владелец из назначения, не произвольный игрок.')
    contentReleaseId: str = Field(min_length=1,max_length=128,description='Закреплённый релиз забега.')
    generation: int = Field(ge=1,description='Актуальное поколение сервера.')
    outcome: str = Field(pattern='^(victory|defeat)$',description='victory — победа, defeat — поражение.')
    churchLevel: int = Field(ge=1,le=5,description='Итоговый уровень церкви, от 1 до 5.')
    stats: RunStatsRequest = Field(default_factory=RunStatsRequest,description='Если не передать счётчики, они считаются нулевыми.')


class RenameProfile(StrictBody):
    """Совместимый PUT: ник обязателен, остальные пропущенные поля сохраняются."""
    model_config=ConfigDict(json_schema_extra={'examples':[ex.PROFILE_UPDATE]})
    expectedVersion: int = Field(strict=True,ge=1,lt=2_147_483_647,description='version из последнего GET /v1/profile. При 409 перечитайте профиль.')
    displayName: str = Field(min_length=1,max_length=32,description='Ник: 1–32 символа, до 120 байт UTF-8, без управляющих символов; пробелы по краям удаляются.')
    avatarAssetKey: str|None = Field(default=None,max_length=128,description='Ключ из GET /v1/profile/avatars. null сбрасывает аватар; пропуск сохраняет его.')
    displayTimezoneIana: str|None = Field(default=None,max_length=64,description='Например Europe/Moscow. Явный null не принимается; пропуск сохраняет значение.')
    locale: str|None = Field(default=None,max_length=16,description='Например ru или en-US. Явный null не принимается.')


class UpdateProfile(StrictBody):
    """Частичное изменение: передайте expectedVersion и хотя бы одно редактируемое поле."""
    model_config=ConfigDict(json_schema_extra={'examples':[ex.PROFILE_UPDATE,{'expectedVersion':2,'avatarAssetKey':None}]})
    expectedVersion: int = Field(strict=True,ge=1,lt=2_147_483_647,description='Текущая version из GET /v1/profile; версия меняется также при игровых начислениях.')
    displayName: str|None = Field(default=None,max_length=32,description='Новый ник; явный null не принимается, пропуск сохраняет значение.')
    avatarAssetKey: str|None = Field(default=None,max_length=128,description='Ключ из каталога; null — сбросить, пропуск — сохранить.')
    displayTimezoneIana: str|None = Field(default=None,max_length=64,description='IANA timezone, например Europe/Moscow; явный null не принимается.')
    locale: str|None = Field(default=None,max_length=16,description='Язык, например ru; явный null не принимается.')


class StartRun(StrictBody):
    """Новое намерение запустить dedicated-забег либо безопасный повтор прежнего."""
    model_config=ConfigDict(json_schema_extra={'examples':[ex.START_RUN]})
    requestKey: UUID = Field(description='Создайте UUID один раз и сохраните до ответа. При таймауте повторите тот же ключ; новый ключ означает новое намерение.')


class ClaimLevelReward(StrictBody):
    """Пустое тело {} либо без тела. ID подарка находится в URL; состав выбирает сервер."""
    model_config=ConfigDict(json_schema_extra={'examples':[{}]})


class OpenCase(StrictBody):
    """Покупка/расходование кейса и сохранение одного розыгрыша."""
    model_config=ConfigDict(json_schema_extra={'examples':[{'requestKey':ex.KEY,'paymentMode':'WALLET'},{'requestKey':ex.KEY,'paymentMode':'INVENTORY'}]})
    requestKey: UUID = Field(description='Один UUID на одно открытие. Повтор не оплачивает и не разыгрывает кейс заново.')
    paymentMode: Annotated[str,Field(pattern='^(INVENTORY|WALLET)$')] = Field(default='INVENTORY',description='INVENTORY — потратить принадлежащий игроку кейс; WALLET — оплатить цену из каталога.')


class StartLocalRun(StrictBody):
    """Запуск LOCAL по опубликованной серверной политике; энергия списывается один раз."""
    model_config=ConfigDict(json_schema_extra={'examples':[ex.START_LOCAL]})
    requestKey: UUID = Field(description='Сохраните один UUID до завершения операции. Нельзя повторно использовать ключ dedicated-старта.')
    policyId: str = Field(pattern=r'^[A-Za-z0-9_.:-]{1,128}$',description='ID опубликованной LOCAL-policy от оператора; пример local-river-v1 не создаётся автоматически.')


class FinishLocalRun(StrictBody):
    """Сводка LOCAL. Идентификаторы/контекст скопируйте из start; награды и XP не передаются."""
    model_config=ConfigDict(json_schema_extra={'examples':[ex.FINISH_LOCAL]})
    schemaVersion: int = Field(strict=True,ge=1,le=1,description='Версия формата сводки: сейчас 1.')
    sessionId: UUID = Field(description='sessionId из ответа запуска именно этого забега.')
    policyId: str = Field(pattern=r'^[A-Za-z0-9_.:-]{1,128}$',description='Закреплённая policyId из start.')
    contentReleaseId: str = Field(pattern=r'^[A-Za-z0-9_.:-]{1,128}$',description='contentReleaseId из start.')
    rulesetId: str = Field(pattern=r'^[A-Za-z0-9_.:-]{1,128}$',description='rulesetId из start.')
    mapId: str = Field(pattern=r'^[A-Za-z0-9_.:-]{1,128}$',description='mapId из start.')
    difficulty: str = Field(pattern=r'^[A-Za-z0-9_.:-]{1,32}$',description='difficulty из start.')
    outcome: str = Field(pattern='^(victory|defeat)$',description='Проверяется вместе с churchLevel и лимитами policy.')
    churchLevel: int = Field(strict=True,ge=1,le=5,description='Итоговый уровень церкви, не количество улучшений.')
    durationMs: int = Field(strict=True,ge=0,le=604800000,description='Длительность симуляции в миллисекундах. Сверяется с серверным временем и лимитами policy.')
    stats: RunStatsRequest = Field(description='Подробные счётчики; неизвестные цели/превышение лимитов отклоняются.')


class BuyPrestigeUpgrade(StrictBody):
    """Покупка следующего постоянного уровня по цене из серверного каталога."""
    model_config=ConfigDict(json_schema_extra={'examples':[ex.BUY_PRESTIGE]})
    requestKey: UUID = Field(description='Один UUID на намерение покупки; при таймауте повторяйте тот же ключ.')
    rulesetId: str = Field(pattern=r'^[A-Za-z0-9_.-]{1,128}$',description='Активный rulesetId из GET /v1/prestige-upgrades.')
    code: str = Field(pattern=r'^[A-Za-z0-9_.-]{1,64}$',description='Код улучшения из того же каталога, например hero.attack_damage.')
    expectedLevel: int = Field(strict=True,ge=0,le=5,description='Текущий currentLevel из каталога. Сервер покупает только следующий уровень; 0 означает ещё не куплено.')
