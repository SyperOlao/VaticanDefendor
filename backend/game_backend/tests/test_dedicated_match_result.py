"""Real PostgreSQL/API checks for the extended dedicated path; no second award use case."""
from concurrent.futures import ThreadPoolExecutor
from copy import deepcopy
from uuid import UUID
import pytest
from sqlalchemy import select, func
from .test_progression_lifecycle import xp_game, start, state, count
from vatican_backend.persistence.postgres.tables import RunResultOperation, PlayerXPTransaction, OutboxMessage, RunValidationAudit


def snapshot(stats):
    data = stats.snapshot()
    hero = sum(data['heroKills'].values()); tower = sum(data['towerKills'].values())
    aggregate = dict(enemiesSpawned=hero+tower, enemiesKilled=hero+tower, otherKills=0,
        heroDamageMilliUnits=100_000, towerDamageMilliUnits=50_000,
        heroKills=[{'key':k,'count':v} for k,v in data['heroKills'].items()],
        towerKills=[{'key':k,'count':v} for k,v in data['towerKills'].items()],
        buildingUpgrades=[{'key':k,'count':v} for k,v in data['buildingUpgrades'].items()],
        buildingsBuilt=[{'key':'tower','count':1}], resourcesProduced={'wood':20,'stone':0,'iron':0},
        resourcesSpent={'wood':10,'stone':0,'iron':0})
    return dict(aggregate, schemaVersion=1, complete=True, durationMs=10_000, simulationTick=600,
                buildings=[{'key':'church','count':1},{'key':'tower','count':1}], waves=[dict(deepcopy(aggregate),waveNumber=1)])


def body(world, outcome='victory'):
    legacy=start(world,outcome=outcome)
    return dict(schemaVersion=2, runId=str(legacy.run_id), accountId=str(legacy.account_id),
        serverId=str(world[1].server_id), rulesetId=legacy.content_release_id,
        generation=legacy.generation,contentReleaseId=legacy.content_release_id,
        outcome=outcome,churchLevel=legacy.church_level,statistics=snapshot(legacy.stats))


def post(world, value):
    return world[0].post(f'/internal/servers/{world[1].server_id}/results',json=value,
                         headers={'X-Server-Secret':world[1].secret})


@pytest.mark.parametrize('outcome',['victory','defeat'])
def test_v2_result_reuses_one_xp_settlement_and_reward_outbox(xp_game,outcome):
    value=body(xp_game,outcome)
    first=post(xp_game,value);assert first.status_code==200,first.text
    replay=post(xp_game,value);assert replay.status_code==200 and replay.json()['replayed']
    assert first.json()['permanentReward']==(25 if outcome=='victory' else 0)
    assert first.json()['progression']['xpGranted']==(80 if outcome=='victory' else 60)
    with xp_game[0].app.state.database.transaction() as session:
        assert count(session,RunResultOperation)==count(session,PlayerXPTransaction)==1
        saved=session.get(RunResultOperation,UUID(value['runId']))
        assert saved.result_payload['statistics']['schemaVersion']==1
        analytics=session.scalar(select(OutboxMessage).where(OutboxMessage.destination=='analytics'))
        assert analytics.payload['matchResultSnapshot']['resourcesProduced']['wood']==20
        assert session.scalar(select(func.count()).select_from(OutboxMessage).where(OutboxMessage.destination=='wallet'))==(1 if outcome=='victory' else 0)


def test_conflicting_result_has_committed_audit_and_no_second_award(xp_game):
    value=body(xp_game);assert post(xp_game,value).status_code==200
    value['statistics']['durationMs']+=1
    assert post(xp_game,value).status_code==409
    with xp_game[0].app.state.database.transaction() as session:
        assert count(session,RunResultOperation)==count(session,PlayerXPTransaction)==1
        audits=list(session.scalars(select(RunValidationAudit)))
        assert len(audits)==1 and audits[0].reasons[0]['code']=='DEDICATED_RESULT_CONFLICT'
        assert audits[0].reasons[0]['expectedSha256']!=audits[0].reasons[0]['receivedSha256']


def test_parallel_v2_http_delivery_serializes_one_run(xp_game):
    value=body(xp_game)
    with ThreadPoolExecutor(max_workers=6) as workers:
        responses=list(workers.map(lambda _:post(xp_game,value),range(6)))
    assert all(r.status_code==200 for r in responses),[r.text for r in responses]
    assert sum(not r.json()['replayed'] for r in responses)==1
    assert state(xp_game)['totalXp']==80
    with xp_game[0].app.state.database.transaction() as session:
        assert count(session,RunResultOperation)==count(session,PlayerXPTransaction)==1


@pytest.mark.parametrize('field',['serverId','accountId','contentReleaseId','rulesetId','generation'])
def test_forged_identity_is_rejected_without_side_effects(xp_game,field):
    from uuid import uuid4
    value=body(xp_game)
    value[field] = value[field]+1 if field=='generation' else str(uuid4()) if field.endswith('Id') else 'wrong'
    assert post(xp_game,value).status_code in (404,409)
    with xp_game[0].app.state.database.transaction() as session:
        assert count(session,RunResultOperation)==count(session,PlayerXPTransaction)==0


def test_player_cannot_set_rewards_or_replace_v2_counters(xp_game):
    value=body(xp_game)
    assert xp_game[0].post(f'/internal/servers/{xp_game[1].server_id}/results',json=value).status_code==401
    value['xp']=999999
    assert post(xp_game,value).status_code==422
    value.pop('xp');value['stats']={'heroKills':{'orc':999}}
    assert post(xp_game,value).status_code==422
    value.pop('stats');value['statistics']['enemiesKilled']+=1
    assert post(xp_game,value).status_code==422


def test_durable_rewards_and_analytics_retry_through_existing_consumers(xp_game,wallet_storage,inventory_storage,rating_storage,analytics_actor,tmp_path):
    """Real SQL across all participant stores; projection adapter records actual validated rows."""
    import json
    from datetime import datetime,timedelta,timezone
    from uuid import uuid4
    from vatican_backend.application.errors import ApplicationError
    from vatican_backend.application.messaging import DeliverRewards
    from vatican_backend.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as GameUow
    from vatican_wallet.application.use_cases import WalletUseCases
    from vatican_wallet.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as WalletUow
    from vatican_wallet.persistence.postgres.tables import EconomyTransaction,RunRewardReceipt
    from vatican_wallet.application.errors import ApplicationError as WalletError
    from vatican_inventory.application.use_cases import InventoryUseCases
    from vatican_inventory.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as InventoryUow
    from vatican_rating.application.use_cases import RatingUseCases
    from vatican_rating.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as RatingUow
    from vatican_backend.persistence.postgres.tables import Run,Profile
    value=body(xp_game)
    database=xp_game[0].app.state.database
    # A client disconnect has no authority to alter the existing active Run row.
    reply=post(xp_game,value);assert reply.status_code==200,reply.text
    assert reply.json()['rewardStatus']=='PENDING'
    wallet=WalletUseCases(WalletUow(wallet_storage[0]))
    initial_balance=wallet.balance(xp_game[2])['balance']
    targets={'wallet':wallet,'inventory':InventoryUseCases(InventoryUow(inventory_storage[0]),xp_game[0].app.state.use_cases.weapons),
             'rating':RatingUseCases(RatingUow(rating_storage[0])),'analytics':analytics_actor}
    class Gateway:
        down=True
        lost=True
        wallet_message=None
        def deliver(self,message):
            if self.down:raise ApplicationError(503,'Injected participant outage')
            target=targets[message['destination']]
            ack=target.receive(message['id'],message['event_name'],message['schema_version'],message['payload'])
            if message['destination']=='wallet':
                self.wallet_message=message
                if self.lost:raise ApplicationError(503,'Injected lost acknowledgement after wallet commit')
            return ack['receipt']
    gateway=Gateway()
    native = NativeJournalProbe(xp_game,value,tmp_path)
    def drain():
        # New worker instance for each retry, same persisted outbox.
        worker=DeliverRewards(GameUow(database),gateway)
        for _ in range(12):
            if not worker.once():return
        raise AssertionError('Worker did not quiesce')
    def ready():
        with database.transaction() as session:
            for row in session.scalars(select(OutboxMessage).where(OutboxMessage.published_at.is_(None))):
                row.available_at=datetime.now(timezone.utc)-timedelta(seconds=1)
    drain();assert post(xp_game,value).json()['rewardStatus']=='PENDING'
    native.recover(expect_pending=True)
    gateway.down=False;ready();drain()
    assert post(xp_game,value).json()['rewardStatus']=='PENDING'
    assert wallet.balance(xp_game[2])['balance']==initial_balance+25
    gateway.lost=False;ready();drain()
    complete=post(xp_game,value).json()
    assert complete['rewardStatus']==complete['analyticsStatus']=='COMPLETED'
    native.recover(expect_pending=False)
    # RabbitMQ aliases and parallel retries must still share the PostgreSQL run receipt.
    m=gateway.wallet_message
    # Prepare independent connections before the measured concurrent consumption.
    # Docker/macOS cold handshakes can exceed the service's 3s connect timeout under Unity import.
    from contextlib import ExitStack
    with ExitStack() as stack:
        for _ in range(6):stack.enter_context(wallet_storage[0].engine.connect())
    with ThreadPoolExecutor(max_workers=6) as pool:
        acks=list(pool.map(lambda _:wallet.receive(uuid4(),m['event_name'],m['schema_version'],m['payload']),range(6)))
    assert all(ack['receipt']['amount']==25 for ack in acks)
    with pytest.raises(WalletError) as rejected:
        wallet.receive(uuid4(),m['event_name'],m['schema_version'],dict(m['payload'],amount=26))
    assert rejected.value.status_code==409
    conflict=deepcopy(value);conflict['statistics']['durationMs']+=1
    assert post(xp_game,conflict).status_code==409
    raw=list(analytics_actor.projection.tables['raw_game_events'].values())
    assert json.loads(raw[0]['payload_json'])['matchResultSnapshot']==value['statistics']
    sql={}
    with database.transaction() as session:
        sql.update(runResults=count(session,RunResultOperation),xpTransactions=count(session,PlayerXPTransaction),conflictAudits=count(session,RunValidationAudit),
                   profileVersion=session.get(Profile,xp_game[2]).version,runStatus=session.get(Run,UUID(value['runId'])).status,
                   xp=state(xp_game)['totalXp'])
    with wallet_storage[0].transaction() as session:
        sql.update(walletRunReceipts=count(session,RunRewardReceipt),walletRunLedgerRows=session.scalar(
            select(func.count()).select_from(EconomyTransaction).where(EconomyTransaction.source_type=='RUN_REWARD')))
    assert sql==dict(runResults=1,xpTransactions=1,conflictAudits=1,profileVersion=2,runStatus='completed',xp=80,walletRunReceipts=1,walletRunLedgerRows=1)
    report={'http':{'initial':reply.status_code,'conflictingWalletAmount':rejected.value.status_code,'conflictingRunResult':409},'sql':sql,
            'receipt':complete,'walletInitialBalance':initial_balance,'walletBalance':wallet.balance(xp_game[2])['balance'],
            'unityJournalHttp':native.evidence,'analyticsRawEvents':len(raw),'analyticsMatchSnapshot':json.loads(raw[0]['payload_json'])['matchResultSnapshot']}
    import os
    from pathlib import Path
    destination=Path(os.environ.get('VD_MATCH_REPORT_DIR',str(tmp_path)))
    destination.mkdir(parents=True,exist_ok=True)
    (destination/'sql-api-evidence.json').write_text(json.dumps(report,indent=2,default=str))


def test_actual_unity_snapshot_contract(xp_game):
    import os,json
    from pathlib import Path
    source=os.environ.get('VD_MATCH_CSHARP_RESULT')
    if not source:pytest.skip('Set VD_MATCH_CSHARP_RESULT to MatchResultVerification.Run exported JSON')
    emitted=json.loads(Path(source).read_text())
    assigned=body(xp_game)
    for key in ('runId','accountId','serverId','contentReleaseId','rulesetId','generation'):emitted[key]=assigned[key]
    reply=post(xp_game,emitted)
    assert reply.status_code==200,reply.text
    assert reply.json()['progression']['xpGranted']==52 and reply.json()['permanentReward']==50
    assert post(xp_game,emitted).json()['replayed']


def test_completed_run_identity_conflicts_are_audited(xp_game):
    from uuid import uuid4
    value=body(xp_game);assert post(xp_game,value).status_code==200
    for field in ('serverId','accountId','generation','contentReleaseId','rulesetId'):
        changed=dict(value)
        changed[field]=changed[field]+1 if field=='generation' else str(uuid4())
        assert post(xp_game,changed).status_code==409
    with xp_game[0].app.state.database.transaction() as session:
        rows=list(session.scalars(select(RunValidationAudit)))
        assert len(rows)==5
        assert all(row.reasons[0]['expectedSha256']!=row.reasons[0]['receivedSha256'] for row in rows)
        assert count(session,PlayerXPTransaction)==1


class NativeJournalProbe:
    """Optional actual Unity processes, no original project import or live account traffic."""
    def __init__(self,world,value,directory):
        import os,json
        self.world,self.value,self.directory=world,value,directory
        self.project=os.environ.get('VD_MATCH_UNITY_PROJECT')
        self.evidence={'executed':False}
        if self.project:
            self.journal=directory/'pending'
            self.journal.mkdir()
            self.path=self.journal/(value['serverId']+'-'+value['runId']+'.json')
            self.path.write_text(json.dumps(value))
    def recover(self,expect_pending):
        if not self.project:return
        import os,json,subprocess,threading
        from http.server import BaseHTTPRequestHandler,ThreadingHTTPServer
        from pathlib import Path
        probe=self;calls=[]
        class Handler(BaseHTTPRequestHandler):
            def log_message(self,*args):pass
            def do_POST(self):
                payload=json.loads(self.rfile.read(int(self.headers['Content-Length'])))
                assert payload==probe.value
                if expect_pending and not calls:
                    code,body=503,{'detail':'Injected backend unavailable before commit'}
                else:
                    reply=probe.world[0].post(self.path,json=payload,headers={'X-Server-Secret':self.headers.get('X-Server-Secret','')})
                    code,body=reply.status_code,reply.json()
                    if expect_pending and len(calls)==1:
                        code,body=503,{'detail':'Injected lost response after committed result'}
                calls.append(code)
                encoded=json.dumps(body).encode()
                self.send_response(code);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(encoded)))
                self.end_headers();self.wfile.write(encoded)
        server=ThreadingHTTPServer(('127.0.0.1',0),Handler)
        thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
        phase='pending' if expect_pending else 'completed'
        log=self.directory/('unity-http-'+phase+'.log')
        environment=dict(os.environ,VD_MATCH_SERVER_ID=self.value['serverId'],VD_MATCH_SERVER_SECRET=self.world[1].secret,
                         VD_MATCH_BACKEND_URL='http://127.0.0.1:'+str(server.server_port),VD_RUN_RESULT_DIR=str(self.journal))
        try:
            result=subprocess.run([os.environ['VD_MATCH_UNITY_EDITOR'],'-batchmode','-nographics','-quit',
                '-standaloneBuildSubtarget','Server','-projectPath',self.project,'-executeMethod',
                'VaticanDefendor.Editor.MatchResultVerification.RecoverJournal','-logFile',str(log)],
                env=environment,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=150)
        finally:server.shutdown();server.server_close();thread.join()
        assert calls,calls
        if expect_pending:
            assert result.returncode!=0 and self.path.exists(),log.read_text()[-4000:]
            assert calls==[503,503,200,200,200],calls
        else:
            assert result.returncode==0 and not self.path.exists(),log.read_text()[-4000:]
            assert calls==[200],calls
        self.evidence.update(executed=True,**{phase:{'httpStatuses':calls,'exitCode':result.returncode,'journalRetained':self.path.exists()}})
        report=Path(os.environ.get('VD_MATCH_REPORT_DIR',str(self.directory)))
        report.mkdir(parents=True,exist_ok=True)
        (report/log.name).write_text(log.read_text())


def test_v5_checkpoint_recovery_fences_old_owner_and_preserves_statistics(xp_game):
    import json,hashlib
    from datetime import datetime,timedelta,timezone
    from vatican_backend.persistence.postgres.tables import GameServer
    value=body(xp_game);client,slot,_,_=xp_game
    checkpoint={'schemaVersion':5,'runId':value['runId'],'simulationTick':600,
                'contentReleaseId':value['contentReleaseId'],'matchStatistics':value['statistics']}
    raw=json.dumps(checkpoint)
    headers={'X-Server-Secret':slot.secret}
    saved=client.post(f'/internal/servers/{slot.server_id}/checkpoint',headers=headers,json={
        'runId':value['runId'],'generation':value['generation'],'schemaVersion':5,'simulationTick':600,
        'snapshotJson':raw,'sha256':hashlib.sha256(raw.encode()).hexdigest()})
    assert saved.status_code==200,saved.text
    with client.app.state.database.transaction() as session:
        session.get(GameServer,slot.server_id).last_seen=datetime.now(timezone.utc)-timedelta(seconds=20)
    recovered=client.post(f'/internal/servers/{slot.server_id}/recover',headers=headers,json={
        'contentReleaseId':value['contentReleaseId'],'protocolVersion':3,'transport':'udp-loopback'})
    assert recovered.status_code==200,recovered.text
    assignment=recovered.json()
    assert assignment['snapshotJson']==raw and assignment['generation']==value['generation']+1
    assert post(xp_game,value).status_code==409
    ack=client.post(f'/internal/servers/{slot.server_id}/ack',headers=headers,json={
        'runId':value['runId'],'generation':assignment['generation']})
    assert ack.status_code==200,ack.text
    value['generation']=assignment['generation']
    reply=post(xp_game,value)
    assert reply.status_code==200 and reply.json()['progression']['xpGranted']==80,reply.text
