"""Reconnect uses existing run/grants/energy/settlement, with actual PostgreSQL."""
from uuid import UUID
from sqlalchemy import select,func
from .test_progression_lifecycle import xp_game,start,apply
from vatican_backend.persistence.postgres.tables import PlayerEnergy,RunResultOperation,PlayerXPTransaction,Run


def test_abandoned_run_renewal_returns_session_expired(xp_game):
    from vatican_backend.application.commands import Assignment
    result=start(xp_game)
    client,slot,account,_=xp_game
    client.app.state.use_cases.runs.abandon(slot.server_id,slot.secret,Assignment(result.run_id,result.generation))
    reply=client.post('/v1/runs/'+str(result.run_id)+'/grant',headers={'Authorization':'Bearer test-token'})
    assert reply.status_code==410,reply.text
    assert reply.json()['code']=='SESSION_EXPIRED'


def test_repeated_renewals_keep_run_energy_and_awards_unchanged(xp_game):
    result=start(xp_game);client,_,account,_=xp_game
    with client.app.state.database.transaction() as session:energy=session.get(PlayerEnergy,account).current_energy
    for _ in range(6):
        reply=client.post('/v1/runs/'+str(result.run_id)+'/grant',headers={'Authorization':'Bearer test-token'})
        assert reply.status_code==200,reply.text
        assert reply.json()['runId']==str(result.run_id)
    with client.app.state.database.transaction() as session:
        assert session.get(PlayerEnergy,account).current_energy==energy
        assert session.scalar(select(func.count()).select_from(Run))==1
        assert session.scalar(select(func.count()).select_from(PlayerXPTransaction))==0
    done=apply(xp_game,result)
    for _ in range(3):
        reply=client.get('/v1/runs/'+str(result.run_id),headers={'Authorization':'Bearer test-token'})
        assert reply.status_code==200 and reply.json()['status']=='completed'
        assert reply.json()['profileVersion']==done['profileVersion']
    with client.app.state.database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(RunResultOperation))==1
        assert session.scalar(select(func.count()).select_from(PlayerXPTransaction))==1


def test_v6_paused_checkpoint_sequence_and_recovery_age_are_server_owned(xp_game):
    import hashlib,json,base64
    from datetime import datetime,timedelta,timezone
    from vatican_backend.persistence.postgres.tables import GameServer,RunCheckpoint
    result=start(xp_game);client,slot,account,_=xp_game
    headers={'X-Server-Secret':slot.secret}
    snapshot={'schemaVersion':6,'checkpointSequence':1,'runId':str(result.run_id),
        'contentReleaseId':result.content_release_id,'simulationTick':100,'connectionHadDisconnected':1,
        'disconnectedSeconds':3,'disconnectRetentionSeconds':30}
    def publish(value):
        raw=json.dumps(value)
        return client.post(f'/internal/servers/{slot.server_id}/checkpoint',headers=headers,json={
            'runId':str(result.run_id),'generation':result.generation,'schemaVersion':6,'simulationTick':100,
            'snapshotJson':raw,'sha256':hashlib.sha256(raw.encode()).hexdigest()})
    assert publish(snapshot).status_code==200
    newer=dict(snapshot,checkpointSequence=2,disconnectedSeconds=5)
    assert publish(newer).status_code==200
    assert publish(newer).json()['replayed']
    assert publish(snapshot).status_code==409
    with client.app.state.database.transaction() as session:
        session.get(GameServer,slot.server_id).last_seen=datetime.now(timezone.utc)-timedelta(seconds=20)
        session.get(RunCheckpoint,result.run_id).created_at=datetime.now(timezone.utc)-timedelta(seconds=23)
        energy=session.get(PlayerEnergy,account).current_energy
    recovered=client.post(f'/internal/servers/{slot.server_id}/recover',headers=headers,json={
        'contentReleaseId':result.content_release_id,'protocolVersion':3,'transport':'udp-loopback'})
    assert recovered.status_code==200,recovered.text
    recovered=recovered.json()
    assert recovered['generation']==result.generation+1
    assert recovered['checkpointAgeSeconds']>=23 and recovered['recoveryDisconnectedSeconds']>=20
    assert json.loads(recovered['snapshotJson'])==newer
    assert client.post(f'/internal/servers/{slot.server_id}/heartbeat',headers=headers,json={
        'runId':str(result.run_id),'generation':result.generation}).status_code==409
    assert client.post(f'/internal/servers/{slot.server_id}/ack',headers=headers,json={
        'runId':str(result.run_id),'generation':recovered['generation']}).status_code==200
    grant=client.post('/v1/runs/'+str(result.run_id)+'/grant',headers={'Authorization':'Bearer test-token'})
    assert grant.status_code==200,grant.text
    raw=base64.urlsafe_b64decode(grant.json()['grant']+'===')
    assert int.from_bytes(raw[53:57],'big')==recovered['generation']
    with client.app.state.database.transaction() as session:assert session.get(PlayerEnergy,account).current_energy==energy


def test_repeated_recovery_cannot_restart_retention_age(xp_game):
    import hashlib,json
    from datetime import datetime,timedelta,timezone
    from vatican_backend.persistence.postgres.tables import GameServer,RunCheckpoint
    result=start(xp_game);client,slot,_,_=xp_game
    headers={'X-Server-Secret':slot.secret}
    raw=json.dumps({'schemaVersion':6,'checkpointSequence':1,'runId':str(result.run_id),
        'contentReleaseId':result.content_release_id,'simulationTick':10,'connectionHadDisconnected':0})
    published=client.post(f'/internal/servers/{slot.server_id}/checkpoint',headers=headers,json={
        'runId':str(result.run_id),'generation':result.generation,'schemaVersion':6,'simulationTick':10,
        'snapshotJson':raw,'sha256':hashlib.sha256(raw.encode()).hexdigest()})
    assert published.status_code==200,published.text
    for attempt in (1,2):
        with client.app.state.database.transaction() as session:
            session.get(GameServer,slot.server_id).last_seen=datetime.now(timezone.utc)-timedelta(seconds=20)
            session.get(RunCheckpoint,result.run_id).created_at=datetime.now(timezone.utc)-timedelta(seconds=40)
        recovered=client.post(f'/internal/servers/{slot.server_id}/recover',headers=headers,json={
            'contentReleaseId':result.content_release_id,'protocolVersion':3,'transport':'udp-loopback'})
        assert recovered.status_code==200,recovered.text
        assert recovered.json()['generation']==result.generation+attempt
        if attempt==2:assert recovered.json()['recoveryDisconnectedSeconds']>=40
