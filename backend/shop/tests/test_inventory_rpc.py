import json
import os
import subprocess
import sys
from types import SimpleNamespace
from uuid import UUID, uuid4

import httpx
import pytest

from vatican_shop.application.errors import ApplicationError, ParticipantConflict
from vatican_shop.application.ports import PurchaseGateway as PurchaseGatewayPort
from vatican_shop.bootstrap.di import create_container
from vatican_shop.bootstrap.rabbit import RabbitSettings
from vatican_shop.bootstrap.settings import Settings
from vatican_shop.domain.inventory_commands import command_id, grant_command, grant_payload
from vatican_shop.integrations.rabbit import RabbitInventoryPublisher
from vatican_shop.integrations.rabbit_purchase import BrokerPurchaseGateway
from vatican_shop.integrations.shop import PurchaseGateway
from .test_gateways import job


class Publisher:
    def __init__(self):
        self.commands = []
        self.failure = None
    def publish(self, command):
        self.commands.append(command)
        if self.failure:
            raise self.failure


def applied(job, **changes):
    return {**grant_payload(job), "status": "APPLIED", "instanceId": str(uuid4()), "inventoryRevision": 2, **changes}


def status(job, outcome="APPLIED", receipt=None):
    identity = str(command_id(job["accountId"], job["operationId"]))
    result = None if outcome == "PENDING" else {"messageId": identity, "status": outcome}
    if outcome == "APPLIED":
        result["receipt"] = receipt or applied(job)
    return {"messageId": identity, "status": outcome, "deliveryGeneration": 1, "result": result}


def gateway(body=None, code=200, publisher=None):
    publisher = publisher or Publisher()
    client = httpx.Client(transport=httpx.MockTransport(lambda request: httpx.Response(code, json=body)))
    return BrokerPurchaseGateway(client, Settings("unused", "s" * 32), publisher), publisher


def test_unknown_command_publishes_stable_source_and_never_confirms_on_publish_alone(job):
    adapter, publisher = gateway(code=404)
    for _ in range(3):
        with pytest.raises(ApplicationError) as error:
            adapter.grant(job)
        assert error.value.status_code == 503
    assert publisher.commands == [grant_command(job)] * 3
    command = publisher.commands[0]
    assert command["payload"]["operationId"] == str(job["operationId"])
    assert command["eventName"] == "inventory.weapon.grant" and command["schemaVersion"] == 1


def test_pending_command_does_not_create_a_publication_storm(job):
    adapter, publisher = gateway(status(job, "PENDING"))
    for _ in range(20):
        with pytest.raises(ApplicationError) as error:
            adapter.grant(job)
        assert error.value.status_code == 503
    assert publisher.commands == []


def test_stored_result_is_available_during_rabbit_outage(job):
    receipt = applied(job)
    publisher = Publisher();publisher.failure = RuntimeError("Private broker endpoint/password")
    adapter, publisher = gateway(status(job, receipt=receipt), publisher=publisher)
    assert adapter.grant(job) == receipt
    assert publisher.commands == []


def test_outer_applied_does_not_hide_a_saved_business_rejection(job):
    receipt = {**grant_payload(job), "status": "REJECTED", "code": "UnknownWeaponConfiguration"}
    adapter, publisher = gateway(status(job, receipt=receipt))
    assert adapter.grant(job) == receipt and publisher.commands == []


@pytest.mark.parametrize("outcome", ["REJECTED", "REVIEW"])
def test_broker_rejection_or_review_requires_reconciliation_instead_of_refund(job, outcome):
    adapter, publisher = gateway(status(job, outcome))
    with pytest.raises(ParticipantConflict):
        adapter.grant(job)
    assert publisher.commands == []


@pytest.mark.parametrize("patch", [
    {"accountId": str(uuid4())}, {"operationId": str(uuid4())}, {"configurationId": "spear.rare"},
    {"instanceId": None}, {"instanceId": "not-uuid"}, {"instanceId": str(UUID(int=0))},
    {"inventoryRevision": True}, {"inventoryRevision": 0}, {"inventoryRevision": 2**63},
    {"status": "PENDING"}, {"status": "REJECTED", "code": "OtherReason"},
])
def test_receipt_must_match_original_purchase_source_and_shape(job, patch):
    adapter, publisher = gateway(status(job, receipt=applied(job, **patch)))
    with pytest.raises(ApplicationError) as error:
        adapter.grant(job)
    assert error.value.status_code == 503 and publisher.commands == []


@pytest.mark.parametrize("patch", [
    {"messageId": str(uuid4())}, {"status": "PROCESSING"}, {"status": {}},
    {"deliveryGeneration": True}, {"deliveryGeneration": 0}, {"result": None},
    {"result": {"messageId": str(uuid4()), "status": "APPLIED"}},
])
def test_invalid_status_never_confirms_or_publishes_another_command(job, patch):
    adapter, publisher = gateway({**status(job), **patch})
    with pytest.raises(ApplicationError) as error:
        adapter.grant(job)
    assert error.value.status_code == 503 and publisher.commands == []


@pytest.mark.parametrize("body", [None, [], 1, "APPLIED", {}])
def test_invalid_json_shape_is_retryable(job, body):
    adapter, publisher = gateway(body)
    with pytest.raises(ApplicationError) as error:
        adapter.grant(job)
    assert error.value.status_code == 503 and publisher.commands == []


@pytest.mark.parametrize("code", [202, 301, 400, 401, 403, 409, 422, 429, 500, 503])
def test_unavailable_status_api_does_not_publish_or_assume_rejection(job, code):
    adapter, publisher = gateway(code=code)
    with pytest.raises(ApplicationError) as error:
        adapter.grant(job)
    assert error.value.status_code == 503 and publisher.commands == []


def test_status_lookup_is_authenticated_and_connection_loss_is_safe(job):
    def handler(request):
        assert request.headers["X-Service-Secret"] == "s" * 32
        assert request.url.path == "/internal/broker/commands/" + grant_command(job)["messageId"]
        raise httpx.ReadTimeout("Private status URL/token", request=request)
    publisher = Publisher()
    with httpx.Client(transport=httpx.MockTransport(handler)) as client:
        adapter = BrokerPurchaseGateway(client, Settings("unused", "s" * 32), publisher)
        with pytest.raises(ApplicationError) as error:
            adapter.grant(job)
    assert error.value.status_code == 503 and "Private" not in error.value.detail and publisher.commands == []


def test_publication_failure_never_exposes_credentials(job):
    publisher = Publisher();publisher.failure = RuntimeError("Private URL/password")
    adapter, _ = gateway(code=404, publisher=publisher)
    with pytest.raises(ApplicationError) as error:
        adapter.grant(job)
    assert error.value.status_code == 503 and "Private" not in error.value.detail


def test_source_is_account_scoped_and_payload_changes_do_not_change_the_message_identity(job):
    original = grant_command(job)
    assert grant_command({**job, "accountId": uuid4()})["messageId"] != original["messageId"]
    changed = grant_command({**job, "plan": {**job["plan"], "configurationId": "spear.rare"}})
    assert changed["messageId"] == original["messageId"] and changed["payload"] != original["payload"]


def test_charge_and_refund_continue_to_use_wallet_http(job):
    def handler(request):
        assert request.url.path in ("/internal/charges", "/internal/refunds")
        return httpx.Response(200, json={**json.loads(request.content), "currencyId": "PRESTIGE", "status": "APPLIED"})
    publisher = Publisher()
    with httpx.Client(transport=httpx.MockTransport(handler)) as client:
        adapter = BrokerPurchaseGateway(client, Settings("unused", "s" * 32), publisher)
        assert adapter.charge(job)["status"] == adapter.refund(job)["status"] == "APPLIED"
    assert publisher.commands == []


class Channel:
    failure = None
    confirmed = None
    def __init__(self):self.calls=[]
    def exchange_declare(self, **kw):self.calls.append(("exchange",kw))
    def queue_declare(self, **kw):self.calls.append(("queue",kw))
    def queue_bind(self, **kw):self.calls.append(("bind",kw))
    def confirm_delivery(self):self.calls.append(("confirm",{}))
    def basic_publish(self, **kw):
        self.calls.append(("publish",kw))
        if self.failure:raise self.failure
        return self.confirmed


@pytest.fixture
def rabbit_connection(monkeypatch):
    channel=Channel()
    class Connection:
        is_open=True
        close_failure=None
        def channel(self):return channel
        def close(self):
            self.is_open=False
            if self.close_failure:raise self.close_failure
    connection=Connection()
    monkeypatch.setattr('vatican_shop.integrations.rabbit.pika.BlockingConnection',lambda params:connection)
    def execute_locally(args, **kwargs):
        request=json.loads(kwargs['input'])
        RabbitInventoryPublisher(RabbitSettings(**request['settings']))._publish_in_process(request['command'])
        return SimpleNamespace(returncode=0)
    monkeypatch.setattr('vatican_shop.integrations.rabbit.subprocess.run',execute_locally)
    return connection,channel


def test_publisher_uses_durable_quorum_persistent_mandatory_and_confirms(job, rabbit_connection):
    connection,channel=rabbit_connection
    publisher=RabbitInventoryPublisher(RabbitSettings("amqp://u:secret@localhost/vhost"))
    publisher.publish(grant_command(job))
    calls=dict(channel.calls)
    assert [name for name,_ in channel.calls]==["exchange","queue","bind","confirm","publish"]
    assert calls["exchange"]=={"exchange":"vatican.commands","exchange_type":"direct","durable":True}
    assert calls["queue"]["durable"] and calls["queue"]["arguments"]=={
        "x-queue-type":"quorum","x-delivery-limit":-1,"x-max-length-bytes":67_108_864,"x-overflow":"reject-publish"}
    sent=calls["publish"];properties=sent["properties"]
    assert sent["mandatory"] and sent["routing_key"]=="inventory"
    assert properties.delivery_mode==2 and properties.content_type=="application/json" and properties.content_encoding=="utf-8"
    assert properties.message_id==properties.correlation_id==grant_command(job)["messageId"]
    assert json.loads(sent["body"])==grant_command(job) and not connection.is_open


@pytest.mark.parametrize("failure", [False, RuntimeError("Private broker password")])
def test_missing_confirm_is_retryable_and_connection_is_closed(job, rabbit_connection, failure):
    connection,channel=rabbit_connection
    if failure is False:channel.confirmed=False
    else:channel.failure=failure
    with pytest.raises(ApplicationError) as error:
        RabbitInventoryPublisher(RabbitSettings("amqp://u:secret@localhost/vhost")).publish(grant_command(job))
    assert error.value.status_code==503 and "Private" not in error.value.detail and not connection.is_open


def test_shutdown_failure_does_not_undo_a_confirmed_publish(job, rabbit_connection):
    connection,_=rabbit_connection;connection.close_failure=RuntimeError("Private socket")
    RabbitInventoryPublisher(RabbitSettings("amqp://u:secret@localhost/vhost")).publish(grant_command(job))
    assert not connection.is_open


def test_publication_process_receives_credentials_only_on_stdin_and_has_a_deadline(job,monkeypatch):
    def execute(args,**kwargs):
        assert args==[sys.executable,'-m','vatican_shop.integrations.rabbit']
        assert 'private-password' not in repr(args)
        assert kwargs['timeout']==12 and kwargs['stdout']==kwargs['stderr']==subprocess.DEVNULL
        assert json.loads(kwargs['input'])['settings']['url']=='amqp://u:private-password@localhost/vhost'
        return SimpleNamespace(returncode=0)
    monkeypatch.setattr('vatican_shop.integrations.rabbit.subprocess.run',execute)
    RabbitInventoryPublisher(RabbitSettings('amqp://u:private-password@localhost/vhost')).publish(grant_command(job))


def test_unconfirmed_process_exit_is_retryable(job,monkeypatch):
    monkeypatch.setattr('vatican_shop.integrations.rabbit.subprocess.run',lambda *args,**kw:SimpleNamespace(returncode=1))
    with pytest.raises(ApplicationError) as error:
        RabbitInventoryPublisher(RabbitSettings('amqp://u:private@localhost/vhost')).publish(grant_command(job))
    assert error.value.status_code==503 and 'private' not in error.value.detail


def test_stalled_publication_is_killed_and_reaped_before_retry(job,monkeypatch,tmp_path):
    original=subprocess.run
    pid_file=tmp_path/'publisher.pid'
    script="import os,time;from pathlib import Path;Path("+repr(str(pid_file))+").write_text(str(os.getpid()));time.sleep(30)"
    def stall(args,**kwargs):
        return original([sys.executable,'-c',script],**kwargs)
    monkeypatch.setattr('vatican_shop.integrations.rabbit.subprocess.run',stall)
    monkeypatch.setattr('vatican_shop.integrations.rabbit.PUBLISH_TIMEOUT_SECONDS',2)
    with pytest.raises(ApplicationError) as error:
        RabbitInventoryPublisher(RabbitSettings('amqp://u:private@localhost/vhost')).publish(grant_command(job))
    assert error.value.status_code==503
    pid=int(pid_file.read_text())
    with pytest.raises(ProcessLookupError):os.kill(pid,0)


def test_publisher_has_its_own_deadline_even_without_a_surviving_parent(job):
    script="""import time
from vatican_shop.integrations import rabbit
rabbit.PUBLISH_TIMEOUT_SECONDS=0.2
rabbit.RabbitInventoryPublisher._publish_in_process=lambda *args:time.sleep(30)
rabbit.main()
"""
    request={'settings':{'url':'amqp://u:secret@localhost/vhost'},'command':grant_command(job)}
    child=subprocess.run([sys.executable,'-c',script],input=json.dumps(request).encode(),
        stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,timeout=5,
        env={**os.environ,'PYTHONPATH':os.pathsep.join(path for path in sys.path if isinstance(path,str))})
    assert child.returncode==2


@pytest.mark.parametrize("transport,type_", [("http",PurchaseGateway),("rabbitmq",BrokerPurchaseGateway)])
def test_dishka_selects_transport_without_connecting_to_broker(transport,type_,monkeypatch):
    def forbidden(*args):pytest.fail("DI construction opened a broker connection")
    monkeypatch.setattr('vatican_shop.integrations.rabbit.pika.BlockingConnection',forbidden)
    settings=Settings("unused","s"*32,inventory_transport=transport,rabbitmq_url="amqp://u:secret@localhost/vhost")
    container=create_container(settings)
    try:assert type(container.get(PurchaseGatewayPort)) is type_
    finally:container.close()


@pytest.mark.parametrize("url", [None, True, 1, "", "http://private:password@localhost", "amqp://", "amqp://host:0", "amqp://host:99999"])
def test_invalid_broker_configuration_fails_early_without_leaking_url(url):
    with pytest.raises(ValueError) as error:Settings("unused","s"*32,inventory_transport="rabbitmq",rabbitmq_url=url)
    assert "private" not in str(error.value) and "password" not in str(error.value)


def test_broker_settings_hide_credentials():
    settings=Settings("private-db","private-service",inventory_transport="rabbitmq",rabbitmq_url="amqp://u:private-password@localhost/vhost")
    assert "private" not in repr(settings) and "private" not in repr(RabbitSettings(settings.rabbitmq_url))


def test_environment_defaults_to_http_and_quotes_shared_credentials(monkeypatch):
    for key in list(os.environ):
        if key.startswith(('VD_SHOP_', 'VD_RABBITMQ_', 'VD_INVENTORY_RABBIT_')):monkeypatch.delenv(key)
    monkeypatch.setenv("VD_SHOP_DATABASE_URL","postgresql://unused/db")
    monkeypatch.setenv("VD_SERVICE_SECRET","s"*32)
    assert Settings.from_environment().inventory_transport=="http"
    values={"VD_SHOP_INVENTORY_TRANSPORT":"rabbitmq","VD_RABBITMQ_USER":"u+name","VD_RABBITMQ_PASSWORD":"p@ss/word",
            "VD_RABBITMQ_HOST":"localhost","VD_RABBITMQ_VHOST":"shop/dev"}
    for key,value in values.items():monkeypatch.setenv(key,value)
    import pika
    settings=Settings.from_environment();parsed=pika.URLParameters(settings.rabbitmq_url)
    assert parsed.credentials.username=="u+name" and parsed.credentials.password=="p@ss/word" and parsed.virtual_host=="shop/dev"
    monkeypatch.setenv("VD_SHOP_RABBITMQ_URL","amqp://explicit:secret@elsewhere/test")
    assert Settings.from_environment().rabbitmq_url=="amqp://explicit:secret@elsewhere/test"
