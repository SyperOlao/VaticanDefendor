import pytest
from vatican_backend import worker
from vatican_backend.application.messaging import DeliverRewards
from vatican_backend.application.runs import RunUseCases
from vatican_backend.application.local_runs import LocalRunUseCases


def test_cleanup_failure_does_not_block_reward_delivery_or_expose_exception_text(monkeypatch,caplog):
    called=[]
    class Runs:
        def expire_pending(self):
            raise RuntimeError('SQL and secret-token must not be logged')
    class Delivery:
        def once(self):
            called.append('delivery')
            return False
    class LocalRuns:
        def maintain(self):
            called.append('local_maintenance')
    class Container:
        def get(self,type_):
            return {RunUseCases:Runs(),LocalRunUseCases:LocalRuns(),DeliverRewards:Delivery()}[type_]
        def close(self):
            called.append('container_closed')
    class Client:
        def close(self):
            called.append('client_closed')
    monkeypatch.setattr(worker.Settings,'from_environment',lambda:None)
    monkeypatch.setattr(worker,'create_game_container',lambda settings:(Container(),Client()))
    monkeypatch.setattr('sys.argv',['worker','--once'])
    with pytest.raises(SystemExit) as error:
        worker.main()
    assert error.value.code==1
    assert called==['local_maintenance','delivery','container_closed','client_closed']
    assert 'secret-token' not in caplog.text
    assert 'RuntimeError' in caplog.text


def test_local_recovery_failure_does_not_block_reward_delivery(monkeypatch,caplog):
    called=[]
    class Runs:
        def expire_pending(self):called.append('assignments')
    class LocalRuns:
        def maintain(self):raise RuntimeError('private LOCAL payload must not be logged')
    class Delivery:
        def once(self):called.append('delivery');return False
    class Container:
        def get(self,type_):return {RunUseCases:Runs(),LocalRunUseCases:LocalRuns(),DeliverRewards:Delivery()}[type_]
        def close(self):called.append('closed')
    class Client:
        def close(self):pass
    monkeypatch.setattr(worker.Settings,'from_environment',lambda:None)
    monkeypatch.setattr(worker,'create_game_container',lambda settings:(Container(),Client()))
    monkeypatch.setattr('sys.argv',['worker','--once'])
    with pytest.raises(SystemExit) as error:worker.main()
    assert error.value.code==1 and called==['assignments','delivery','closed']
    assert 'private LOCAL payload' not in caplog.text
