"""Opt-in multi-process load/fault test; use ../scripts/load_check.py to own infrastructure."""
from collections import Counter, defaultdict
from concurrent.futures import ThreadPoolExecutor
from copy import deepcopy
from pathlib import Path
import hashlib
import json
import math
import os
import random
import socket
import subprocess
import sys
from threading import Event, Lock
import time
from uuid import UUID, uuid4, uuid5

import httpx
import pika
import psycopg
import pytest
from sqlalchemy import func, select
from sqlalchemy.exc import SQLAlchemyError

from vatican_rating.application.use_cases import RatingUseCases
from vatican_rating.integrations.rabbit import BrokerUnavailable, RabbitBroker
from vatican_rating.persistence.postgres.tables import (
    BrokerInbox, BrokerRejection, ConsumedMessage, Leaderboard, LeaderboardScore, OutboxMessage, RunScoreReceipt,
)
from vatican_rating.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from vatican_backend.persistence.postgres.tables import OutboxMessage as GameOutbox, RatingResultMessage

SECRET = 'isolated-rating-load-check-' + 's' * 40
pytestmark = pytest.mark.skipif(not os.getenv('VD_RATING_LOAD_RUNS'), reason='Opt-in owned load/fault infrastructure')

# Exercise the actual game application adapters without requiring unrelated wallet/inventory services.
GAME_LOOP = '''
import os,time
from vatican_rating.bootstrap.rabbit import RabbitSettings
from vatican_backend.application.messaging import DeliverRewards
from vatican_backend.application.rating_broker import AcceptRatingResult
from vatican_backend.integrations.rating_rabbit import RatingRabbitBroker,RatingRabbitDelivery
from vatican_backend.persistence.postgres import Database
from vatican_backend.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
uow=PostgresUnitOfWorkFactory(Database(os.environ['LOAD_GAME_DB']))
class CountedBroker(RatingRabbitBroker):
 def publish_command(self,message):
  super().publish_command(message)
  print('PUBLISH_CONFIRMED',flush=True)
broker=CountedBroker(RabbitSettings.from_environment())
class NoHTTP:
 def deliver(self,message):raise AssertionError('HTTP fallback in rating load check')
producer=DeliverRewards(uow,RatingRabbitDelivery(broker,NoHTTP()))
receiver=AcceptRatingResult(uow)
while True:
 try:
  worked=producer.once() if os.environ['LOAD_ROLE']=='producer' else broker.receive_once(receiver)
  if not worked:time.sleep(.025)
 except Exception as error:
  print(type(error).__name__,flush=True)
  broker.close();time.sleep(.2)
'''


def free_port():
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        return listener.getsockname()[1]


def test_concurrent_duplicates_and_service_restarts_preserve_every_unique_run(
        service_storage_factory, rabbit_settings, tmp_path):
    count = int(os.getenv('VD_RATING_LOAD_RUNS', '0'))
    if not count:
        pytest.skip('Opt-in: python backend/rating/scripts/load_check.py --runs 1000')
    clients = int(os.getenv('VD_RATING_LOAD_CLIENTS', '32'))
    owner = os.environ['VD_RATING_LOAD_OWNER']
    containers = [os.environ['VD_TEST_RABBITMQ_CONTAINER'], os.environ['VD_TEST_RATING_POSTGRES_CONTAINER']]
    for name in containers:
        label = subprocess.check_output(['docker', 'inspect', name, '--format',
            '{{index .Config.Labels "vatican.rating.load-test"}}'], text=True).strip()
        assert label == owner, 'Refusing to restart a container not owned by this load check'
    report_path = Path(os.environ['VD_RATING_LOAD_REPORT'])
    rating_db, rating_url = service_storage_factory('rating')
    game_db, game_url = service_storage_factory('game_backend')
    api = RatingUseCases(PostgresUnitOfWorkFactory(rating_db))
    boards = ['run-xp', 'load-season-2', 'load-season-3', 'load-season-4']
    for board in boards[1:]:
        api.configure_board(board)
    randomizer = random.Random(20261117)
    accounts = [str(UUID(int=randomizer.getrandbits(128), version=4)) for _ in range(32)]
    bodies, http_bodies, broker_bodies = [], [], []
    expected_scores = defaultdict(int)
    for index in range(count):
        run = UUID(int=randomizer.getrandbits(128), version=4)
        payload = {'accountId': accounts[0] if index % 3 else accounts[index % len(accounts)],
            'runId': str(run), 'boardCode': boards[index % 4], 'rulesetId': 'xp-v1',
            'contentReleaseId': 'load-release-v1', 'points': 0 if index % 11 == 0 else 1 + index % 97}
        body = {'messageId': str(uuid5(run, 'load.original')), 'eventName': 'run.rating', 'schemaVersion': 1, 'payload': payload}
        bodies.append(body)
        expected_scores[(payload['boardCode'], payload['accountId'])] += payload['points']
        if index % 4 in (0, 1):
            http_bodies += [body, deepcopy(body), {**body, 'messageId': str(uuid5(run, 'load.http-alias'))}]
        if index % 4 != 0:
            broker_bodies += [body, {**body, 'messageId': str(uuid5(run, 'load.amqp-alias'))}]
    randomizer.shuffle(http_bodies)
    randomizer.shuffle(broker_bodies)
    with game_db.transaction() as session:
        session.add_all([GameOutbox(id=UUID(body['messageId']), aggregate_id=UUID(body['payload']['runId']),
            destination='rating', event_name='run.rating', schema_version=1, payload=body['payload']) for body in broker_bodies])
    expected_messages = {body['messageId']: body['payload'] for body in http_bodies + broker_bodies}
    expected_broker = {body['messageId'] for body in broker_bodies}
    processes, definitions, logs, faults = {}, {}, [], []
    started = time.monotonic()
    deadline = started + max(240, count / 10)
    stopped, lock = Event(), Lock()
    statistics, receipts, latencies = Counter(), {}, []
    report = {'result': 'FAILED', 'seed': 20261117, 'uniqueRuns': count, 'clients': clients,
        'ratingWorkers': 4, 'apiReplicas': 2, 'accounts': len(accounts), 'boards': len(boards), 'faults': faults}
    environment = {**os.environ, 'VD_SERVICE_SECRET': SECRET, 'VD_RATING_DATABASE_URL': rating_url,
        'VD_RATING_RABBITMQ_URL': rabbit_settings.url,
        'VD_RATING_RABBIT_QUEUE_PREFIX': rabbit_settings.queue_prefix,
        'VD_RATING_RABBIT_EXCHANGE_PREFIX': rabbit_settings.exchange_prefix,
        'LOAD_GAME_DB': game_url, 'VD_RATING_DB_POOL_SIZE': '10',
        'VD_RATING_DB_LOCK_TIMEOUT_MS': '1000', 'VD_RATING_DB_STATEMENT_TIMEOUT_MS': '5000',
        'VD_AUTH_GRPC_TARGET': '127.0.0.1:50051', 'VD_AUTH_GRPC_CA_FILE': '',
        'VD_AUTH_GRPC_TIMEOUT_MS': '2000', 'VD_AUTH_GRPC_ATTEMPTS': '3'}

    def start(name, command, extra=None):
        definitions[name] = (command, extra)
        log = (tmp_path / (name + '.log')).open('ab')
        logs.append(log)
        processes[name] = subprocess.Popen(command, env={**environment, **(extra or {})}, stdout=log, stderr=log)

    def stop(name, crash=False):
        process = processes.pop(name)
        if process.poll() is None:
            (process.kill if crash else process.terminate)()
            try:
                process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                process.kill(); process.wait(timeout=5)

    def restart(name):
        command, extra = definitions[name]
        stop(name, crash=True)
        start(name, command, extra)

    def snapshot():
        with rating_db.transaction() as session:
            runs = session.scalar(select(func.count()).select_from(RunScoreReceipt))
            inbox = dict(session.execute(select(BrokerInbox.status, func.count()).group_by(BrokerInbox.status)).all())
            pending_events = session.scalar(select(func.count()).select_from(OutboxMessage).where(OutboxMessage.published_at.is_(None)))
        with game_db.transaction() as session:
            completed = session.scalar(select(func.count()).select_from(GameOutbox).where(GameOutbox.published_at.is_not(None)))
        return {'runs': runs, 'inbox': inbox, 'pendingEvents': pending_events, 'gameCompleted': completed}

    def wait_for(predicate, timeout, description):
        until = time.monotonic() + timeout
        while time.monotonic() < until:
            for name, process in processes.items():
                assert process.poll() is None, 'Unexpected child exit: ' + name
            try:
                if predicate():
                    return
            except (psycopg.Error, SQLAlchemyError, httpx.HTTPError):
                pass
            time.sleep(.1)
        raise AssertionError('Timed out: ' + description)

    ports = [free_port(), free_port()]
    urls = [f'http://127.0.0.1:{port}' for port in ports]
    client = httpx.Client(timeout=4, trust_env=False, limits=httpx.Limits(max_connections=clients + 4, max_keepalive_connections=clients))
    http_pool, publisher_pool = ThreadPoolExecutor(max_workers=clients), ThreadPoolExecutor(max_workers=4)
    broker_admin = RabbitBroker(rabbit_settings)

    def send_http(body):
        begin, attempt = time.monotonic(), 0
        while not stopped.is_set() and time.monotonic() < deadline:
            attempt += 1
            try:
                reply = client.post(urls[(UUID(body['messageId']).int + attempt) % 2] + '/internal/messages',
                    headers={'X-Service-Secret': SECRET}, json=body)
                with lock:
                    statistics['httpStatus' + str(reply.status_code)] += 1
                if reply.status_code == 200:
                    result = reply.json()
                    assert result['messageId'] == body['messageId'] and result['status'] == 'APPLIED'
                    receipt = result['receipt']
                    assert all(receipt[key] == value for key, value in body['payload'].items())
                    with lock:
                        previous = receipts.setdefault(body['payload']['runId'], receipt)
                        assert previous == receipt, 'Replay changed a frozen run receipt'
                        statistics['httpCompleted'] += 1
                        statistics['httpRetries'] += attempt - 1
                        latencies.append(time.monotonic() - begin)
                    return
                assert reply.status_code in (408, 429, 503), 'Unexpected HTTP status: ' + str(reply.status_code)
            except httpx.HTTPError:
                with lock:
                    statistics['httpConnectionErrors'] += 1
            time.sleep(.03)
        assert stopped.is_set(), 'HTTP delivery did not recover within the load deadline'

    def publish_duplicates(chunk):
        broker = RabbitBroker(rabbit_settings)
        try:
            for body in chunk:
                for _ in range(2):
                    while not stopped.is_set() and time.monotonic() < deadline:
                        try:
                            broker.publish_command(json.dumps(body).encode())
                            with lock:
                                statistics['extraConfirmedCommands'] += 1
                            break
                        except Exception:
                            with lock:
                                statistics['publisherRetries'] += 1
                            broker.close()
                            time.sleep(.1)
                    else:
                        assert stopped.is_set(), 'AMQP publisher did not recover'
        finally:
            broker.close()

    def crash_container(name, description):
        state = snapshot()
        assert state['gameCompleted'] < len(broker_bodies), 'Fault was not injected under active delivery load'
        subprocess.run(['docker', 'kill', '--signal', 'KILL', name], check=True, capture_output=True, timeout=15)
        subprocess.run(['docker', 'start', name], check=True, capture_output=True, timeout=20)
        faults.append({'service': description, 'kind': 'SIGKILL/restart', 'atSeconds': round(time.monotonic() - started, 3),
                       'before': state})
        print('fault:', description, state, flush=True)

    try:
        for index, port in enumerate(ports):
            start('api' + str(index), [sys.executable, '-m', 'uvicorn', 'vatican_rating.presentation.app:create_app',
                '--factory', '--host', '127.0.0.1', '--port', str(port), '--no-access-log'])
        wait_for(lambda: all(client.get(url + '/health/ready').status_code == 200 for url in urls), 20, 'API startup')
        for index in range(4):
            start('worker' + str(index), [sys.executable, '-m', 'vatican_rating.worker'])
        for role in ('producer', 'consumer'):
            for index in range(2):
                start(role + str(index), [sys.executable, '-c', GAME_LOOP], {'LOAD_ROLE': role})
        http_jobs = [http_pool.submit(send_http, body) for body in http_bodies]
        publisher_jobs = [publisher_pool.submit(publish_duplicates, broker_bodies[index::4]) for index in range(4)]
        wait_for(lambda: statistics['httpCompleted'] >= 10, 20, 'HTTP activity')
        active = sum(not job.done() for job in http_jobs)
        assert active > 0, 'API restart missed active HTTP workload'
        restart('api0')
        faults.append({'service': 'rating API replica', 'kind': 'SIGKILL/restart', 'httpOutstanding': active,
                       'atSeconds': round(time.monotonic() - started, 3)})
        wait_for(lambda: snapshot()['inbox'].get('APPLIED', 0) >= 4, 30, 'AMQP processing')
        state = snapshot()
        for name in ('worker0', 'worker1', 'worker2', 'worker3', 'producer0', 'consumer0'):
            restart(name)
        faults.append({'service': 'all 4 rating workers, game producer and result consumer', 'kind': 'SIGKILL/restart',
                       'atSeconds': round(time.monotonic() - started, 3), 'before': state})
        crash_container(containers[0], 'RabbitMQ')
        # Crash PostgreSQL before its producers/consumers have confirmed every source.
        crash_container(containers[1], 'PostgreSQL')
        for future in http_jobs + publisher_jobs:
            future.result(timeout=max(1, deadline - time.monotonic()))
        stable, last_print = 0, 0
        while time.monotonic() < deadline:
            try:
                state = snapshot()
                broker_admin.connect()
                depths = {name: broker_admin.channel.queue_declare(queue=name, passive=True).method.message_count
                          for name in (rabbit_settings.commands_queue, rabbit_settings.results_queue, rabbit_settings.dead_queue)}
            except (SQLAlchemyError, psycopg.Error, BrokerUnavailable, pika.exceptions.AMQPError):
                stable = 0
                broker_admin.close()
                time.sleep(.1)
                continue
            done = (state['runs'] == count and state['inbox'] == {'APPLIED': len(expected_broker)}
                    and state['gameCompleted'] == len(broker_bodies) and state['pendingEvents'] == 0 and not any(depths.values()))
            stable = stable + 1 if done else 0
            if time.monotonic() - last_print >= 10:
                print('drain:', state, 'queued:', sum(depths.values()), flush=True)
                last_print = time.monotonic()
            if stable >= 6:
                break
            for name, process in processes.items():
                assert process.poll() is None, 'Worker/API died without restart: ' + name
            time.sleep(.5)
        else:
            raise AssertionError('Load did not drain: ' + json.dumps(state))
        # No producers remain; freeze the verified state and release unacknowledged sockets.
        for name in list(processes):
            if not name.startswith('api'):
                stop(name)
        with rating_db.transaction() as session:
            scores = {(code, str(account)): score for code, account, score in session.execute(
                select(Leaderboard.code, LeaderboardScore.account_id, LeaderboardScore.score)
                .join(LeaderboardScore, LeaderboardScore.board_id == Leaderboard.id))}
            assert scores == dict(expected_scores), 'Lost or duplicated score increments'
            stored = list(session.scalars(select(RunScoreReceipt)))
            assert len(stored) == count
            stored_by_run = {str(row.run_id): row.receipt_json for row in stored}
            assert all(stored_by_run[run] == receipt for run, receipt in receipts.items())
            consumed = list(session.scalars(select(ConsumedMessage)))
            assert {str(row.message_id) for row in consumed} == set(expected_messages)
            for row in consumed:
                expected_hash = hashlib.sha256(json.dumps(['run.rating', 1, expected_messages[str(row.message_id)]],
                    sort_keys=True, separators=(',', ':')).encode()).digest()
                assert row.payload_hash == expected_hash
            assert session.scalar(select(func.count()).select_from(BrokerRejection)) == 0
            events = list(session.scalars(select(OutboxMessage)))
            assert len(events) == len(expected_broker)
            assert all(row.published_at and row.failed_at is None for row in events)
        with game_db.transaction() as session:
            sources = list(session.scalars(select(GameOutbox)))
            assert len(sources) == len(broker_bodies)
            assert all(row.published_at and row.failed_at is None and row.receipt_json == stored_by_run[row.payload['runId']] for row in sources)
            results = list(session.scalars(select(RatingResultMessage)))
            assert len(results) == len(expected_broker)
            assert all(row.outcome == 'APPLIED' for row in results), 'Quarantined/conflicting game result under load'
        groups = defaultdict(list)
        for row in stored:
            groups[(row.receipt_json['boardCode'], str(row.account_id))].append(row.receipt_json)
        for key, group in groups.items():
            cursor, boundaries = 0, {0}
            for receipt in sorted((r for r in group if r['points']), key=lambda r: r['scoreAfter']):
                assert receipt['scoreAfter'] - receipt['points'] == cursor, 'Gap/overlap in receipt cumulative balances'
                cursor = receipt['scoreAfter']; boundaries.add(cursor)
            assert cursor == expected_scores[key]
            assert all(r['scoreAfter'] in boundaries for r in group if r['points'] == 0)
        for board in boards:
            public = client.get(urls[0] + '/v1/leaderboards/' + board + '?limit=100').json()['entries']
            expected = sorted(((account, score) for (code, account), score in expected_scores.items() if code == board),
                              key=lambda row: (-row[1], UUID(row[0])))
            assert public == [{'accountId': account, 'score': score, 'position': index + 1}
                              for index, (account, score) in enumerate(expected)]
        # Changed payloads must remain conflicts after all crashes and successful replays.
        conflict = deepcopy(bodies[0]); conflict['payload']['points'] += 1
        for identity in (conflict['messageId'], str(uuid4())):
            conflict['messageId'] = identity
            assert client.post(urls[0] + '/internal/messages', headers={'X-Service-Secret': SECRET}, json=conflict).status_code == 409
        with rating_db.transaction() as session:
            after_conflicts = {(code, str(account)): score for code, account, score in session.execute(
                select(Leaderboard.code, LeaderboardScore.account_id, LeaderboardScore.score)
                .join(LeaderboardScore, LeaderboardScore.board_id == Leaderboard.id))}
            assert after_conflicts == scores
            assert session.scalar(select(func.count()).select_from(ConsumedMessage)) == len(consumed)
            assert session.scalar(select(func.count()).select_from(RunScoreReceipt)) == len(stored)
        producer_confirms = sum(path.read_text().count('PUBLISH_CONFIRMED') for path in tmp_path.glob('producer*.log'))
        report.update(result='PASSED', expectedXP=sum(expected_scores.values()), actualXP=sum(scores.values()),
            receipts=len(stored), consumedMessages=len(consumed), gameConfirmed=len(sources), resultEvents=len(results),
            httpLogicalRequests=len(http_bodies), brokerSourceCommands=len(broker_bodies),
            observedConfirmedCommandCopies=statistics['extraConfirmedCommands'] + producer_confirms,
            observedGameProducerConfirms=producer_confirms, counters=dict(statistics),
            latencySeconds={str(p): round(sorted(latencies)[max(0, math.ceil(len(latencies) * p / 100) - 1)], 4)
                            for p in (50, 95, 99)}, final=state)
    except BaseException as error:
        report['error'] = str(error) if isinstance(error, AssertionError) else type(error).__name__
        raise
    finally:
        stopped.set()
        http_pool.shutdown(wait=True, cancel_futures=True)
        publisher_pool.shutdown(wait=True, cancel_futures=True)
        for name in list(processes):
            stop(name)
        client.close(); broker_admin.close()
        for log in logs:
            log.close()
        report['elapsedSeconds'] = round(time.monotonic() - started, 3)
        report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n')
        print('load report:', report_path, report['result'], flush=True)
