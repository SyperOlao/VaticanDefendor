"""Opt-in actual Unity players + UDP fault relay + FastAPI + isolated PostgreSQL stores."""
from contextlib import contextmanager
from dataclasses import replace
from pathlib import Path
from threading import Thread,Event
from uuid import UUID
import asyncio,json,os,select,socket,subprocess,time
import pytest
from sqlalchemy import select as sql_select,func

pytestmark=pytest.mark.skipif(not os.environ.get('VD_RECOVERY_PROJECT'),reason='Set VD_RECOVERY_PROJECT and build Mac server/client in that isolated project')


def wait_for(read,predicate,timeout=45):
    deadline=time.monotonic()+timeout;last=None
    while time.monotonic()<deadline:
        last=read()
        if predicate(last):return last
        time.sleep(.15)
    raise AssertionError(f'Timeout: {last!r}')


class Relay:
    """Packet fault fixture only: unchanged UTP datagrams, one upstream socket per client."""
    def __init__(self,target):
        self.listener=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);self.listener.bind(('127.0.0.1',0))
        self.port=self.listener.getsockname()[1];self.target=target;self.stop=Event();self.drop=Event();self.drop_downstream=Event()
        self.upstream={};self.downstream={};self.dropped=0
        self.thread=Thread(target=self.run,daemon=True);self.thread.start()
    def run(self):
        while not self.stop.is_set():
            readable,_,_=select.select([self.listener,*self.downstream],[],[],.1)
            for source in readable:
                payload,address=source.recvfrom(65535)
                if self.drop.is_set() or source is not self.listener and self.drop_downstream.is_set():self.dropped+=1;continue
                if source is self.listener:
                    if address not in self.upstream:
                        upstream=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);upstream.bind(('127.0.0.1',0))
                        self.upstream[address]=upstream;self.downstream[upstream]=address
                    self.upstream[address].sendto(payload,self.target)
                else:self.listener.sendto(payload,self.downstream[source])
    def close(self):
        self.stop.set();self.thread.join(2)
        self.listener.close()
        for item in self.downstream:item.close()


class Player:
    def __init__(self,case,role,name,extra=(),env_overrides=None):
        self.case,self.role,self.name=case,role,name
        self.command=case.directory/(name+'-command.json');self.output=case.directory/(name+'-state.json')
        self.log=case.directory/(name+'.log');self.stop=case.directory/(name+'-stop');self.nonce=0
        self.output.unlink(missing_ok=True);self.command.unlink(missing_ok=True);self.stop.unlink(missing_ok=True)
        project=Path(os.environ['VD_RECOVERY_PROJECT'])
        executable=(project/'Builds/OnlineMac/Server/VaticanDefendor.app/Active Combat' if role=='server' else
                    project/'Builds/OnlineMac/Client/VaticanDefendor.app/Contents/MacOS/Active Combat')
        assert executable.exists(),executable
        env=dict(os.environ,VD_BACKEND_URL=case.url,VD_SERVER_SECRET=case.slot.secret,VD_GUEST_TOKEN='test-token',
            VD_RECOVERY_COMMAND_FILE=str(self.command),VD_RECOVERY_OUTPUT_FILE=str(self.output),
            VD_RUN_RESULT_DIR=str(case.journal),VD_CHECKPOINT_SECONDS='1',VD_SESSION_GRACE_SECONDS='1',
            VD_SESSION_RETENTION_SECONDS=str(case.retention),VD_SESSION_AFTER_GRACE='pause',VD_SESSION_ON_EXPIRY='abandon')
        env.update(env_overrides or {})
        env.pop('VD_CONTENT_RELEASE_ID',None)
        args=[str(executable),'-batchmode','-nographics','-logFile',str(self.log),'-vdRole',role,
              '-vdBackendUrl',case.url,'-vdInsecureLocalBackend','-vdProbeRecovery']
        if role=='server':args+=['-vdServerId',str(case.slot.server_id),'-vdPort',str(case.server_port),'-vdShutdownFile',str(self.stop)]
        self.process=subprocess.Popen(args+list(extra),env=env,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        case.players.append(self)
    def read(self):
        if self.output.exists():
            try:return json.loads(self.output.read_text())
            except json.JSONDecodeError:pass
        return {'processExitCode':self.process.poll(),'log':str(self.log)}
    def wait(self,predicate,timeout=45):
        def live():
            if self.process.poll() is not None:
                raise AssertionError(f'{self.name} exited {self.process.returncode}: '+self.log.read_text(errors='replace')[-2500:])
            return self.read()
        return wait_for(live,predicate,timeout)
    def write(self,action,**values):
        self.nonce+=1
        temporary=self.command.with_suffix('.tmp');temporary.write_text(json.dumps(dict(nonce=self.nonce,action=action,**values)))
        temporary.replace(self.command)
    def send(self,action,**values):
        self.write(action,**values)
        return self.wait(lambda x:x.get('commandNonce',0)>=self.nonce)
    def kill(self):self.process.kill();self.process.wait(10)
    def close(self):
        if self.process.poll() is None:
            if self.role=='server':
                self.stop.touch()
                try:self.process.wait(8);return
                except subprocess.TimeoutExpired:pass
            self.process.terminate()
            try:self.process.wait(8)
            except subprocess.TimeoutExpired:self.kill()


@pytest.fixture
def process_case(backend,wallet_storage,inventory_storage,rating_storage,analytics_actor,tmp_path):
    import uvicorn
    from vatican_backend.application.progression import ConfigureProgression
    from vatican_backend.domain.progression import ProgressionRules,XPRule,LevelDefinition
    from vatican_backend.bootstrap.settings import RewardPolicy
    from vatican_backend.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as GameUow
    from vatican_wallet.application.use_cases import WalletUseCases
    from vatican_wallet.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as WalletUow
    from vatican_inventory.application.use_cases import InventoryUseCases
    from vatican_inventory.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as InventoryUow
    from vatican_rating.application.use_cases import RatingUseCases
    from vatican_rating.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as RatingUow
    from vatican_backend.application.messaging import DeliverRewards
    client,settings,slot=backend
    class Case:pass
    case=Case();case.client=client;case.players=[];case.retention=120
    case.directory=Path(os.environ.get('VD_RECOVERY_REPORT_DIR',str(tmp_path)))/tmp_path.name
    case.directory.mkdir(parents=True,exist_ok=True);case.journal=case.directory/'pending';case.journal.mkdir(exist_ok=True)
    sock=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);sock.bind(('127.0.0.1',0));case.server_port=sock.getsockname()[1];sock.close()
    case.relay=Relay(('127.0.0.1',case.server_port))
    case.slot=replace(slot,port=case.relay.port)
    metadata=json.loads(Path(os.environ['VD_RECOVERY_METADATA']).read_text())
    case.release=metadata['contentReleaseId'];case.protocol=metadata['protocolVersion']
    client.app.state.use_cases.runs.settings=replace(settings,servers={slot.server_id:case.slot},reward_policies={case.release:RewardPolicy(25)})
    ConfigureProgression(GameUow(client.app.state.database)).execute(ProgressionRules(case.release,
        (XPRule('HERO_KILL','*',5,50),XPRule('TOWER_KILL','*',2,20),XPRule('RUN_VICTORY','*',20,20)),(LevelDefinition(1,0),)))
    case.account=UUID(client.app.state.test_player['accountId'])
    case.down=Event();case.result_failures=[];case.delay_grants=Event();case.delayed_grants=[];case.http_events=[];case.heartbeat_truncate=Event();case.heartbeat_delay=Event();case.heartbeat_injections=[]
    class Faults:
        async def __call__(self,scope,receive,send):
            started=time.monotonic();request_bytes=0;status=None
            original_receive,original_send=receive,send
            async def measured_receive():
                nonlocal request_bytes
                message=await original_receive();request_bytes+=len(message.get('body',b''));return message
            async def measured_send(message):
                nonlocal status
                if message['type']=='http.response.start':status=message['status']
                if (scope['type']=='http' and scope['path'].endswith('/heartbeat') and
                    message['type']=='http.response.body' and message.get('body') and case.heartbeat_truncate.is_set()):
                    case.heartbeat_truncate.clear();case.heartbeat_injections.append({'kind':'truncated','at':time.time()})
                    # The real endpoint has committed. Leave its Content-Length intact and
                    # omit the last response byte, exercising unknown-outcome TCP failure.
                    await original_send(dict(message,body=message['body'][:-1],more_body=True))
                    return
                await original_send(message)
            receive,send=measured_receive,measured_send
            if scope['type']=='http' and scope['path'].endswith('/heartbeat') and case.heartbeat_delay.is_set():
                case.heartbeat_delay.clear();case.heartbeat_injections.append({'kind':'timeout','at':time.time()})
                await asyncio.sleep(12)
            if scope['type']=='http' and scope['path'].endswith('/grant') and case.delay_grants.is_set():
                case.delayed_grants.append(scope['path'])
                while case.delay_grants.is_set():await asyncio.sleep(.1)
            if scope['type']=='http' and scope['path'].endswith('/results') and case.down.is_set():
                case.result_failures.append(scope['path'])
                await send({'type':'http.response.start','status':503,'headers':[(b'content-type',b'application/json')]})
                await send({'type':'http.response.body','body':b'{"detail":"Injected unavailable result endpoint"}'})
            else:await client.app(scope,receive,send)
            if scope['type']=='http':case.http_events.append({'at':time.time(),'path':scope['path'],
                'status':status,'requestBytes':request_bytes,'seconds':time.monotonic()-started})
    listener=socket.socket();listener.bind(('127.0.0.1',0));listener.listen(128)
    case.url='http://127.0.0.1:'+str(listener.getsockname()[1])
    http=uvicorn.Server(uvicorn.Config(Faults(),log_level='error',lifespan='off'))
    thread=Thread(target=lambda:http.run(sockets=[listener]),daemon=True);thread.start()
    wait_for(lambda:http.started,lambda x:x,10)
    case.database=client.app.state.database
    case.wallet_database=wallet_storage[0]
    case.wallet=WalletUseCases(WalletUow(wallet_storage[0]))
    initial=case.wallet.balance(case.account)['balance'];case.initial_balance=initial
    targets={'wallet':case.wallet,'inventory':InventoryUseCases(InventoryUow(inventory_storage[0]),client.app.state.use_cases.weapons),
             'rating':RatingUseCases(RatingUow(rating_storage[0])),'analytics':analytics_actor}
    class Delivery:
        def deliver(self,message):
            reply=targets[message['destination']].receive(message['id'],message['event_name'],message['schema_version'],message['payload'])
            return reply['receipt']
    def drain():
        worker=DeliverRewards(GameUow(case.database),Delivery())
        for _ in range(15):
            if not worker.once():return
    case.drain=drain
    from vatican_backend.persistence.postgres.tables import Run,GameServer,RunCheckpoint,RunResultOperation,PlayerEnergy,PlayerXPTransaction
    def db():
        with case.database.transaction() as session:
            runs=list(session.scalars(sql_select(Run).where(Run.account_id==case.account).order_by(Run.created_at.desc())))
            current=runs[0] if runs else None
            checkpoint=session.get(RunCheckpoint,current.id) if current else None
            server=session.get(GameServer,slot.server_id)
            return {'runs':len(runs),'runId':str(current.id) if current else None,'status':current.status if current else None,
                'generation':current.generation if current else None,'serverStatus':server.status if server else None,
                'checkpoint':json.loads(checkpoint.snapshot_json) if checkpoint else None,
                'results':session.scalar(sql_select(func.count()).select_from(RunResultOperation)),
                'xpTransactions':session.scalar(sql_select(func.count()).select_from(PlayerXPTransaction)),
                'energy':session.get(PlayerEnergy,case.account).current_energy if session.get(PlayerEnergy,case.account) else None}
    case.db=db
    try:yield case
    finally:
        for player in reversed(case.players):player.close()
        case.relay.close();http.should_exit=True;thread.join(5);listener.close()


def test_actual_process_disconnect_recovery_and_pending_result(process_case):
    c=process_case;events=[]
    def record(name,value):events.append({'name':name,'value':value});(c.directory/'events.json').write_text(json.dumps(events,indent=2))
    server=Player(c,'server','server-1')
    wait_for(c.db,lambda x:x['serverStatus']=='ready')
    first=Player(c,'client','client-a')
    connected=first.wait(lambda x:x.get('connection')=='InGame' and x.get('heroes')==1)
    server.wait(lambda x:x.get('heroes')==1)
    initial=c.db();assert initial['runs']==1 and initial['energy']==95
    record('initial',{'client':connected,'db':initial})
    server.wait(lambda x:x.get('enemies',0)>0,25)
    # Successful construction and authoritative replay are checked at actual placements.
    built=False
    for x,z in [(4,4),(-4,4),(6,4),(-6,4),(4,6),(-4,6),(8,4),(4,-4)]:
        previous=first.read().get('operation',0)
        first.send('build',definition='structure.wall.default',x=x,z=z)
        result=first.wait(lambda d:d.get('operation',0)>previous)
        if result['lastOutcome']=='Built':built=True;break
        time.sleep(.6)
    assert built,result
    first.send('pause');paused=server.wait(lambda x:x.get('paused')==1)
    pinned=first.wait(lambda x:x.get('paused')==1)
    c.relay.drop.set();time.sleep(1);c.relay.drop.clear()
    assert server.read()['phase']=='Active'
    assert first.read()['connection']=='InGame'
    record('short-blackout-no-defeat',server.read())
    c.relay.drop.set()
    first.wait(lambda x:x.get('connection')=='Lost',60)
    server.wait(lambda x:x.get('owner')==0 and x.get('paused')==1,10)
    c.relay.drop.clear()
    first.send('retry')
    returned=first.wait(lambda x:x.get('connection')=='InGame' and x.get('heroes')==1)
    assert returned['runId']==connected['runId'] and returned['structures']==pinned['structures'] and returned['wood']==pinned['wood']
    record('udp-disconnect-and-retry',returned)
    # Pause/resume uses the same presenter lifecycle operation as ClientShellView.
    first.send('suspend');server.wait(lambda x:x.get('owner')==0)
    first.send('resume');first.wait(lambda x:x.get('connection')=='InGame' and x.get('heroes')==1)
    first.send('recreate')
    recreated=first.wait(lambda x:x.get('connection')=='InGame' and x.get('heroes')==1 and x.get('commandNonce',0)>=first.nonce)
    assert recreated['runId']==connected['runId'] and recreated['structures']==pinned['structures']
    assert recreated['world']!=returned['world']
    record('client-world-recreated',recreated)
    # A second authenticated client takes over; the old connection is fenced.
    second=Player(c,'client','client-b')
    current=second.wait(lambda x:x.get('connection')=='InGame' and x.get('heroes')==1)
    first.wait(lambda x:x.get('connection')=='Lost')
    owner=server.wait(lambda x:x.get('owner')==current['owner'] and x.get('eligibleConnections')==1)
    assert owner['heroes']==1 and owner['structures']==pinned['structures']
    first.send('move',x=1,z=0);time.sleep(.5)
    assert abs(server.read()['x']-owner['x'])<.001
    record('two-clients-single-owner',owner)
    # Freeze a checkpoint and forcibly kill only this server process.
    saved=wait_for(c.db,lambda x:x['checkpoint'] is not None and x['checkpoint'].get('checkpointSequence',0)>0)
    server.send('capture')
    server.kill();assert server.process.returncode==-9
    second.wait(lambda x:x.get('connection')=='Lost',60)
    restored_server=Player(c,'server','server-2')
    restored=restored_server.wait(lambda x:x.get('generation')==saved['generation']+1 and x.get('heroes')==1,50)
    second.send('retry');after=second.wait(lambda x:x.get('connection')=='InGame' and x.get('heroes')==1)
    checkpoint=saved['checkpoint']
    assert restored['runId']==saved['runId'] and restored['structures']==len(checkpoint['structures'])+1
    assert after['structures']==pinned['structures'] and after['wood']==checkpoint['wood']
    assert c.db()['runs']==1 and c.db()['energy']==95 and c.db()['results']==0
    record('sigkill-checkpoint-restored',{'server':restored,'client':after,'db':c.db()})
    # Completed result must survive backend outage and another process crash.
    c.down.set();restored_server.send('finish',outcome='victory')
    restored_server.wait(lambda x:x.get('phase')=='ResultPending')
    wait_for(lambda:list(c.journal.glob('*.json')),lambda x:len(x)==1)
    assert c.db()['results']==0
    wait_for(lambda:len(c.result_failures),lambda x:x>0,15)
    restored_server.kill();assert restored_server.process.returncode==-9
    second.wait(lambda x:x.get('connection')=='Lost',60)
    replay_server=Player(c,'server','server-3')
    time.sleep(2);assert len(list(c.journal.glob('*.json')))==1 and c.db()['results']==0
    c.down.clear();wait_for(c.db,lambda x:x['results']==1)
    second.send('retry')
    second.wait(lambda x:x.get('phase')=='ResultPending' and x.get('backendResult')==1)
    assert len(list(c.journal.glob('*.json')))==1
    c.drain()
    completed=second.wait(lambda x:x.get('phase')=='Completed' and x.get('backendResult')==1,30)
    wait_for(lambda:list(c.journal.glob('*.json')),lambda x:not x,30)
    state=c.db();assert state['runs']==state['results']==state['xpTransactions']==1 and state['energy']==95
    assert c.wallet.balance(c.account)['balance']==c.initial_balance+25
    record('pending-result-restart-confirmed',{'client':completed,'db':state,'walletBalance':c.wallet.balance(c.account)['balance'],'droppedDatagrams':c.relay.dropped,'result503Responses':len(c.result_failures)})


def test_actual_retention_expiry_is_not_defeat_or_second_start(process_case):
    c=process_case;c.retention=4
    server=Player(c,'server','expiry-server')
    wait_for(c.db,lambda x:x['serverStatus']=='ready')
    client=Player(c,'client','expiry-client')
    client.wait(lambda x:x.get('connection')=='InGame' and x.get('heroes')==1)
    client.send('disconnect')
    wait_for(c.db,lambda x:x['status']=='failed',20)
    client.send('retry')
    expired=client.wait(lambda x:x.get('connection')=='Error' and x.get('screen')=='Error')
    assert 'SessionExpired' in expired['message']
    state=c.db();assert state['runs']==1 and state['energy']==95 and state['results']==state['xpTransactions']==0
    (c.directory/'expiry-evidence.json').write_text(json.dumps({'client':expired,'db':state},indent=2))


def test_actual_pending_build_survives_client_world_recreation(process_case):
    c=process_case
    server=Player(c,'server','pending-server')
    wait_for(c.db,lambda x:x['serverStatus']=='ready')
    client=Player(c,'client','pending-client')
    original=client.wait(lambda x:x.get('connection')=='InGame' and x.get('heroes')==1)
    built=False
    for x,z in [(4,4),(-4,4),(6,4),(-6,4),(4,6),(-4,6),(8,4),(4,-4)]:
        previous=server.read().get('operation',0)
        c.relay.drop_downstream.set()
        client.send('build',definition='structure.wall.default',x=x,z=z)
        committed=server.wait(lambda d:d.get('operation',0)>previous)
        if committed['lastOutcome']=='Built':built=True;break
        c.relay.drop_downstream.clear()
        client.wait(lambda d:d.get('operation',0)>previous and d.get('pending')==0)
        time.sleep(.6)
    assert built,committed
    pending=client.wait(lambda x:x.get('pending')==1 and x.get('pendingOperation')==committed['operation'])
    assert pending['operation']<committed['operation']
    client.send('disconnect');c.relay.drop_downstream.clear()
    server.wait(lambda x:x.get('owner')==0)
    client.send('recreate')
    restored=client.wait(lambda x:x.get('connection')=='InGame' and x.get('heroes')==1 and x.get('pending')==0 and x.get('operation')==committed['operation'])
    after=server.read()
    assert restored['world']!=original['world']
    assert after['wood']==committed['wood'] and after['structures']==committed['structures'] and after['heroes']==1
    server.send('capture')
    checkpoint=json.loads(Path(str(server.output)+'.checkpoint.json').read_text())
    assert sum(x['count'] for x in checkpoint['matchStatistics']['buildingsBuilt'])==1
    (c.directory/'pending-build-evidence.json').write_text(json.dumps({'unknownAcknowledgement':pending,'beforeWorld':original['world'],
        'afterWorld':restored['world'],'committedServer':committed,'afterRetryServer':after,'ledger':checkpoint['matchStatistics']},indent=2))


def test_actual_old_generation_process_stops_after_backend_fence(process_case):
    from datetime import datetime,timedelta,timezone
    from vatican_backend.persistence.postgres.tables import GameServer
    c=process_case
    server=Player(c,'server','fenced-server')
    wait_for(c.db,lambda x:x['serverStatus']=='ready')
    client=Player(c,'client','fenced-client')
    client.wait(lambda x:x.get('connection')=='InGame' and x.get('heroes')==1)
    client.send('pause');server.wait(lambda x:x.get('paused')==1)
    saved=wait_for(c.db,lambda x:x['checkpoint'] is not None)
    with c.database.transaction() as session:
        session.get(GameServer,c.slot.server_id).last_seen=datetime.now(timezone.utc)-timedelta(seconds=20)
    recovered=c.client.post(f'/internal/servers/{c.slot.server_id}/recover',headers={'X-Server-Secret':c.slot.secret},json={
        'contentReleaseId':c.release,'protocolVersion':c.protocol,'transport':'udp-loopback','prestigeSchemaVersion':1})
    assert recovered.status_code==200,recovered.text
    assert recovered.json()['generation']==saved['generation']+1
    assert server.process.wait(timeout=12)==0
    assert 'Backend fenced this server generation' in server.log.read_text()
    assert '[Bootstrap] World disposed:' in server.log.read_text()
    state=c.db();assert state['runs']==1 and state['energy']==95 and state['results']==state['xpTransactions']==0
    (c.directory/'fence-evidence.json').write_text(json.dumps({'db':state,'oldServerExitCode':server.process.returncode},indent=2))


def test_actual_world_recreation_cancels_pending_grant_without_blocking(process_case):
    c=process_case
    server=Player(c,'server','cancel-server')
    wait_for(c.db,lambda x:x['serverStatus']=='ready')
    client=Player(c,'client','cancel-client')
    original=client.wait(lambda x:x.get('connection')=='InGame' and x.get('heroes')==1)
    client.send('pause');server.wait(lambda x:x.get('paused')==1)
    client.send('disconnect');server.wait(lambda x:x.get('owner')==0)
    c.delay_grants.set()
    client.send('retry')
    wait_for(lambda:len(c.delayed_grants),lambda n:n==1,10)
    client.write('recreate')
    try:
        # The second request proves Unity's main loop stayed live through Dispose.
        wait_for(lambda:len(c.delayed_grants),lambda n:n>=2,10)
    finally:c.delay_grants.clear()
    returned=client.wait(lambda x:x.get('connection')=='InGame' and x.get('heroes')==1)
    assert returned['world']!=original['world']
    state=c.db();assert state['runs']==1 and state['energy']==95 and state['results']==0
    (c.directory/'cancel-evidence.json').write_text(json.dumps({'delayedGrantRequests':len(c.delayed_grants),'client':returned,'db':state},indent=2))


def test_actual_active_wave_reconnect_preserves_spawn_identity(process_case):
    c = process_case
    server = Player(c, 'server', 'active-wave-server')
    wait_for(c.db, lambda state: state['serverStatus'] == 'ready')
    client = Player(c, 'client', 'active-wave-client')
    connected = client.wait(lambda state: state.get('connection') == 'InGame' and state.get('heroes') == 1)
    server.wait(lambda state: state.get('enemies', 0) > 0 and state.get('paused') == 0)
    wait_for(c.db, lambda state: state['checkpoint'] and state['checkpoint']['wavePhase'] == 2)
    server.send('capture')
    before = json.loads(Path(str(server.output) + '.checkpoint.json').read_text())
    before_live = server.read()
    assert before['manualPaused'] == before['automaticPaused'] == 0
    server.send('disconnect')
    client.wait(lambda state: state.get('connection') == 'Lost')
    server.wait(lambda state: state.get('owner') == 0)
    client.send('retry')
    returned = client.wait(lambda state: state.get('connection') == 'InGame' and state.get('heroes') == 1)
    current = server.wait(lambda state: state.get('heroes') == 1 and state.get('eligibleConnections') == 1 and
                         state.get('paused') == 0)
    server.send('capture')
    after = json.loads(Path(str(server.output) + '.checkpoint.json').read_text())
    assert returned['runId'] == connected['runId'] == before['runId'] == after['runId']
    assert current['phase'] == 'Active' and after['simulationTick'] > before['simulationTick']
    assert after['totalSpawned'] == before['totalSpawned']
    original_ids = {actor['stableId'] for actor in before['targets']}
    restored_ids = [actor['stableId'] for actor in after['targets']]
    assert len(restored_ids) == len(set(restored_ids)) and set(restored_ids) <= original_ids
    assert current['structures'] <= before_live['structures']
    before_structures = {item['stableId'] for item in before['structures']}
    after_structures = [item['stableId'] for item in after['structures']]
    assert len(after_structures) == len(set(after_structures)) and set(after_structures) <= before_structures
    state = c.db()
    assert state['runs'] == 1 and state['energy'] == 95 and state['results'] == state['xpTransactions'] == 0
    (c.directory / 'active-wave-evidence.json').write_text(json.dumps(dict(
        before=before, after=after, server=current, client=returned, db=state), indent=2))


def test_actual_completed_run_recycles_world_in_same_process(process_case):
    """Disposal of method-scoped queries must finish before Restart destroys the World."""
    c=process_case
    server=Player(c,'server','recycle-server')
    wait_for(c.db,lambda x:x['serverStatus']=='ready')
    worlds=[];runs=[]
    for index in range(3):
        client=Player(c,'client',f'recycle-client-{index}')
        client.wait(lambda x:x.get('connection')=='InGame' and x.get('heroes')==1)
        state=server.wait(lambda x:x.get('phase')=='Active' and x.get('runId') not in runs)
        worlds.append(state['world']);runs.append(state['runId'])
        assert server.process.poll() is None
        if index==2:break
        server.send('finish',outcome='defeat')
        wait_for(c.db,lambda x:x['results']==index+1)
        c.drain()
        client.wait(lambda x:x.get('phase')=='Completed')
        client.close()
        wait_for(c.db,lambda x:x['serverStatus']=='ready',45)
    state=c.db()
    assert len(set(worlds))==len(set(runs))==3
    assert state['runs']==3 and state['results']==2
    (c.directory/'recycle-evidence.json').write_text(json.dumps({'pid':server.process.pid,
        'worlds':worlds,'runIds':runs,'db':state},indent=2))



def test_actual_heartbeat_transport_retry_and_timeout_recovery(process_case):
    c=process_case
    server=Player(c,'server','heartbeat-server')
    wait_for(c.db,lambda x:x['serverStatus']=='ready')
    client=Player(c,'client','heartbeat-client')
    client.wait(lambda x:x.get('connection')=='InGame' and x.get('heroes')==1)
    client.send('pause');server.wait(lambda x:x.get('paused')==1)
    c.heartbeat_truncate.set()
    wait_for(lambda:c.heartbeat_injections,lambda x:any(e['kind']=='truncated' for e in x),15)
    wait_for(lambda:server.log.read_text(),lambda x:'Backend heartbeat transport retry:' in x,15)
    time.sleep(2)
    assert 'Backend heartbeat unavailable:' not in server.log.read_text()
    # A timeout must close the join gate, then resume the same heartbeat loop.
    c.heartbeat_delay.set()
    wait_for(lambda:c.heartbeat_injections,lambda x:any(e['kind']=='timeout' for e in x),15)
    wait_for(lambda:server.log.read_text(),lambda x:'Backend heartbeat unavailable:' in x,20)
    unavailable=server.log.read_text().index('Backend heartbeat unavailable:')
    wait_for(lambda:server.log.read_text()[unavailable:],lambda x:'Backend assignment heartbeat restored.' in x,20)
    assert server.process.poll() is None and client.read()['connection']=='InGame'
    state=c.db();assert state['runs']==1 and state['energy']==95 and state['results']==0
    (c.directory/'heartbeat-evidence.json').write_text(json.dumps({'injections':c.heartbeat_injections,
        'http':c.http_events,'db':state},indent=2))
