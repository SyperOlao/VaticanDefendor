"""Complete command examples and payload schemas for Swagger, without runtime changes."""
from copy import deepcopy
from typing import Annotated, Literal
from uuid import UUID, uuid5

from pydantic import BaseModel, ConfigDict, Field, TypeAdapter

from .documentation import ACCOUNT, OPERATION, REQUEST, REWARD, CHANGE, ex
from ..application.catalog import CurrencyCode, EventName

Positive = Annotated[int,Field(strict=True,ge=1,le=9_223_372_036_854_775_807)]
Index = Annotated[int,Field(strict=True,ge=0,le=2_147_483_647)]


class Payload(BaseModel):
    model_config=ConfigDict(extra='forbid')
    accountId: UUID = Field(description='ID владельца кошелька из auth. Внутренний метод вызывается доверенным сервисом.')


class PurchasePayload(Payload):
    operationId: UUID = Field(description='Постоянный ID покупки. Для возврата используйте этот же ID и ту же сумму.')
    amount: Positive = Field(description='Целое количество PRESTIGE; больше нуля. Не строка, не дробь и не true.')


class RunRewardPayload(Payload):
    runId: UUID = Field(description='ID забега. За один забег начисление выполняется один раз даже при новом messageId.')
    amount: Positive = Field(description='Количество начисляемого престижа, больше нуля.')


class RewardPayload(Payload):
    itemIndex: Index = Field(description='Индекс валютной позиции в наборе наград, начиная с 0.')
    rewardType: Literal['CURRENCY'] = Field(description='Кошелёк обрабатывает только валютную позицию набора.')
    rewardRef: CurrencyCode = Field(description='Код начисляемой валюты: PRESTIGE или GEMS.')
    quantity: Positive = Field(description='Сумма начисления в целых единицах валюты.')


class LevelRewardPayload(RewardPayload):
    claimId: UUID = Field(description='ID получения подарка. Пара claimId + itemIndex определяет неизменяемую позицию награды.')


class CaseRewardPayload(RewardPayload):
    openingId: UUID = Field(description='ID открытия кейса. Пара openingId + itemIndex определяет позицию награды.')


class CasePaymentPayload(Payload):
    openingId: UUID = Field(description='Постоянный ID оплачиваемого открытия кейса.')
    caseCode: str = Field(pattern=r'^[A-Za-z0-9_.-]{1,64}$',description='Код кейса из игрового каталога.')
    currencyId: CurrencyCode = Field(description='Валюта списания: PRESTIGE или GEMS.')
    amount: Positive = Field(description='Цена открытия; больше нуля.')


class PrestigePaymentPayload(Payload):
    purchaseId: UUID = Field(description='Постоянный ненулевой ID покупки постоянного улучшения.')
    code: str = Field(pattern=r'^[A-Za-z0-9_.-]{1,64}$',description='Код улучшения из правил игры.')
    rulesetId: str = Field(pattern=r'^[A-Za-z0-9_.-]{1,128}$',description='Версия правил покупки улучшения.')
    fromLevel: Annotated[int,Field(strict=True,ge=0,lt=5)] = Field(description='Уровень улучшения до покупки.')
    toLevel: Annotated[int,Field(strict=True,ge=1,le=5)] = Field(description='Новый уровень: строго fromLevel + 1.')
    amount: Positive = Field(description='Списываемая сумма престижа.')
    currencyId: Literal['PRESTIGE'] = Field(description='Постоянные улучшения оплачиваются престижем.')


class PrestigeRefundPayload(Payload):
    resetId: UUID
    purchaseIds: list[UUID] = Field(min_length=1, max_length=40, description='Успешно оплаченные уровни.')
    amount: Positive = Field(description='Сумма исходных списаний.')
    currencyId: Literal['PRESTIGE']


class Command(BaseModel):
    model_config=ConfigDict(extra='forbid')
    messageId: UUID = Field(description='ID доставки. Для повтора сохраняйте UUID и всё тело; для новой команды нужен новый UUID.')
    schemaVersion: Literal[1] = Field(description='Поддерживается версия 1; передавайте целое число.')


class RunRewardCommand(Command):
    eventName: Literal['run.currency_reward']
    payload: RunRewardPayload

class ChargeCommand(Command):
    eventName: Literal['wallet.purchase.charge']
    payload: PurchasePayload

class RefundCommand(Command):
    eventName: Literal['wallet.purchase.refund']
    payload: PurchasePayload

class LevelRewardCommand(Command):
    eventName: Literal['level.reward']
    payload: LevelRewardPayload

class CaseRewardCommand(Command):
    eventName: Literal['case.reward']
    payload: CaseRewardPayload

class CasePaymentCommand(Command):
    eventName: Literal['case.payment']
    payload: CasePaymentPayload

class PrestigePaymentCommand(Command):
    eventName: Literal['prestige-upgrade.payment']
    payload: PrestigePaymentPayload


class PrestigeRefundCommand(Command):
    eventName: Literal['prestige-upgrade.refund']
    payload: PrestigeRefundPayload


WalletCommand = Annotated[RunRewardCommand|ChargeCommand|RefundCommand|LevelRewardCommand|CaseRewardCommand|CasePaymentCommand|PrestigePaymentCommand|PrestigeRefundCommand,
                          Field(discriminator='eventName')]


def message(name,payload):
    return {'messageId':str(uuid5(UUID(REQUEST),name)),'eventName':str(name),'schemaVersion':1,'payload':{'accountId':ACCOUNT,**payload}}


COMMAND_EXAMPLES={
    EventName.RUN_CURRENCY:ex('Начислить награду за завершённый забег',deepcopy(REWARD)),
    EventName.PURCHASE_CHARGE:ex('Списать 25 престижа за покупку',message(EventName.PURCHASE_CHARGE,{'operationId':OPERATION,'amount':25})),
    EventName.PURCHASE_REFUND:ex('Вернуть сумму той же покупки',message(EventName.PURCHASE_REFUND,{'operationId':OPERATION,'amount':25})),
    EventName.LEVEL_REWARD:ex('Начислить валютную позицию подарка',message(EventName.LEVEL_REWARD,{'claimId':REQUEST,'itemIndex':0,'rewardType':'CURRENCY','rewardRef':'GEMS','quantity':10})),
    EventName.CASE_REWARD:ex('Начислить валютную позицию награды кейса',message(EventName.CASE_REWARD,{'openingId':REQUEST,'itemIndex':0,'rewardType':'CURRENCY','rewardRef':'PRESTIGE','quantity':15})),
    EventName.CASE_FUNDING:ex('Оплатить открытие кейса',message(EventName.CASE_FUNDING,{'openingId':REQUEST,'caseCode':'recruit','currencyId':'PRESTIGE','amount':20})),
    EventName.PRESTIGE_PAYMENT:ex('Оплатить один уровень постоянного улучшения',message(EventName.PRESTIGE_PAYMENT,
        {'purchaseId':REQUEST,'code':'hero.health','rulesetId':'rules-1','fromLevel':0,'toLevel':1,'amount':100,'currencyId':'PRESTIGE'})),
    EventName.PRESTIGE_REFUND:ex('Возвратить оплату за постоянное улучшение',message(EventName.PRESTIGE_REFUND,
        {'resetId':OPERATION,'purchaseIds':[REQUEST],'amount':100,'currencyId':'PRESTIGE'})),
}


def install_message_examples(app):
    original=app.openapi
    def openapi():
        document=original()
        schema=TypeAdapter(WalletCommand).json_schema(ref_template='#/components/schemas/{model}')
        document['components']['schemas'].update(schema.pop('$defs',{}))
        operation=document['paths']['/internal/messages']['post']
        media=operation['requestBody']['content']['application/json']
        media.update(schema=schema,examples=deepcopy(COMMAND_EXAMPLES))
        base=operation['description'].split('\n\nВыберите нужную команду',1)[0]
        operation['description']=base+'''

Выберите нужную команду в **Request body → Examples**. payload и обязательные поля зависят от eventName.
У примеров разные messageId: это разные доставки. Для повторной отправки сохраняйте выбранный messageId и всё тело.

- Награды: runId либо claimId/openingId + itemIndex защищают от повторного начисления.
- Покупка: списание и возврат используют один operationId и одну сумму; возврат однократный.
- Недостаток средств — сохранённый бизнес-отказ: HTTP 200 и внешний APPLIED означают обработку команды.
  Для покупки проверяйте receipt.status, для кейса — receipt.outcome, для постоянного улучшения — receipt.outcome/rejectionCode.
- При повторе сохранённая квитанция может содержать прежний баланс. Текущий баланс читайте через GET /v1/wallet.
'''
        operation['responses']['200']['content']['application/json']['examples']['chargeRejected']=ex(
            'Команда обработана, но средств недостаточно',{
                'messageId':COMMAND_EXAMPLES[EventName.PURCHASE_CHARGE]['value']['messageId'],
                'status':'APPLIED','receipt':{**CHANGE,'status':'REJECTED','code':'InsufficientFunds'}})
        transaction=str(uuid5(UUID(ACCOUNT),'wallet:PURCHASE:'+OPERATION))
        history=document['paths']['/v1/wallet/transactions']['get']
        explanation='\n\nДля первой страницы оставьте before пустым. Для следующей передайте nextCursor в before с той же валютой. nextCursor=null — конец. История не создаёт кошелёк.'
        history['description']=history['description'].split('\n\nДля первой страницы',1)[0]+explanation
        history['responses']['200']['content']['application/json']['examples']['nextPage']=ex(
            'Страница limit=1; есть более старые записи',{'transactions':[{
                'transactionId':transaction,'currencyId':'PRESTIGE','delta':-25,'balanceAfter':475,
                'reason':'PURCHASE','sourceType':'PURCHASE','sourceId':OPERATION,'itemIndex':0,
                'createdAt':'2026-11-17T10:00:00+00:00'}],'nextCursor':transaction})
        for param in history.get('parameters',[]):
            if param['name']=='before':param['description']='Курсор nextCursor из предыдущего ответа с тем же фильтром валюты. Для первой страницы не заполняйте.'
            if param['name']=='currencyId':param['description']='Необязательный фильтр валюты: PRESTIGE или GEMS. При смене валюты начинайте без before.'
        document['components']['schemas'].pop('Message',None)
        return document
    app.openapi=openapi
