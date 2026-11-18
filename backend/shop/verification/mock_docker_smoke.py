"""Opt-in isolated Docker/HTTP smoke. Never connects to the ordinary Compose stack.

Build these tags first from the repository root:
  docker build -t vatican-shop:mock-verify-20261117 backend/shop
  docker build -t vatican-inventory:mock-verify-20261117 backend/inventory
  docker build -t vatican-game:mock-verify-20261117 backend/game_backend
Run with a Python environment containing httpx. All created resources are ephemeral.
Auth is an explicitly local test stub; Game, Shop, Inventory and worker use built code.
"""
from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
import secrets
import subprocess
import tempfile
import time
from uuid import uuid4

import httpx


AUTH = '''
import json,os
from http.server import BaseHTTPRequestHandler,HTTPServer
class Handler(BaseHTTPRequestHandler):
 def do_GET(self):
  token=self.headers.get('Authorization','').removeprefix('Bearer ')
  if self.path!='/v1/auth/me' or token not in ('admin','user','other','guest'):
   self.send_response(401);self.end_headers();return
  body=json.dumps({'accountId':os.environ['TEST_OTHER'] if token=='other' else os.environ['TEST_USER'],
    'isGuest':token=='guest','roles':['ADMIN'] if token in ('admin','guest') else []}).encode()
  self.send_response(200);self.send_header('Content-Type','application/json');self.end_headers();self.wfile.write(body)
 def log_message(self,*args):pass
HTTPServer(('0.0.0.0',8080),Handler).serve_forever()
'''


def main():
    suffix = uuid4().hex[:10]
    prefix = 'vd-mock-http-' + suffix
    network = prefix + '-net'
    names = []
    temporary = tempfile.TemporaryDirectory(prefix='vatican-mock-http-')
    directory = Path(temporary.name)
    keys = {name: secrets.token_urlsafe(40) for name in ('service', 'database', 'control', 'webhook', 'receipt', 'grant')}
    user, other, bundle = uuid4(), uuid4(), uuid4()

    def docker(*args, data=None):
        result = subprocess.run(['docker', *args], input=data, capture_output=True, text=True, timeout=120)
        if result.returncode:
            # Do not include env files or raw external stderr in errors.
            raise RuntimeError('Docker operation failed: ' + args[0])
        return result.stdout.strip()

    def envfile(label, values):
        file = directory / (label + '.env')
        file.write_text(''.join(key + '=' + str(value) + '\n' for key, value in values.items()))
        file.chmod(0o600)
        return str(file)

    def start(label, image, env, command=(), published=False):
        name = prefix + '-' + label
        names.append(name)
        args = ['run', '-d', '--name', name, '--label', 'vatican.test.owner=' + prefix,
                '--network', network, '--network-alias', label, '--env-file', env]
        if published:
            args += ['-p', '127.0.0.1::8080']
        docker(*args, image, *command)
        if published:
            return 'http://127.0.0.1:' + docker('port', name, '8080/tcp').rsplit(':', 1)[1]
        return name

    def wait(predicate, label, seconds=45):
        end = time.monotonic() + seconds
        while time.monotonic() < end:
            try:
                result = predicate()
                if result:
                    return result
            except (httpx.HTTPError, RuntimeError):
                pass
            time.sleep(.2)
        raise AssertionError('Timed out: ' + label)

    try:
        docker('network', 'create', '--label', 'vatican.test.owner=' + prefix, network)
        pg = start('pg', 'postgres:16', envfile('pg', {'POSTGRES_USER': 'mock_test',
            'POSTGRES_PASSWORD': keys['database'], 'POSTGRES_DB': 'postgres'}))
        wait(lambda: docker('exec', pg, 'pg_isready', '-U', 'mock_test', '-d', 'postgres'), 'PostgreSQL ready')
        for database in ('game', 'shop', 'inventory'):
            docker('exec', pg, 'createdb', '-U', 'mock_test', database)
        common = {'VD_SERVICE_SECRET': keys['service'], 'VD_GAME_URL': 'http://game:8080',
                  'VD_AUTH_URL': 'http://auth:8080', 'VD_INVENTORY_URL': 'http://inventory:8080'}
        settings = {}
        for service, module in (('game', 'vatican_backend'), ('shop', 'vatican_shop'), ('inventory', 'vatican_inventory')):
            url = 'postgresql://mock_test:' + keys['database'] + '@pg:5432/' + service
            values = {**common, ('VD_DATABASE_URL' if service == 'game' else 'VD_' + service.upper() + '_DATABASE_URL'): url}
            if service in ('shop', 'inventory'):
                values.update(VD_APP_ENVIRONMENT='test', VD_MOCK_GRANT_SECRET=keys['grant'])
                values['VD_' + service.upper() + '_MOCK_PAYMENTS_ENABLED'] = '1'
            if service == 'shop':
                values.update(VD_MOCK_CONTROL_SECRET=keys['control'], VD_MOCK_WEBHOOK_SECRET=keys['webhook'],
                              VD_MOCK_RECEIPT_SECRET=keys['receipt'])
            settings[service] = envfile(service, values)
            docker('run', '--rm', '--network', network, '--env-file', settings[service],
                   'vatican-' + service + ':mock-verify-20261117', 'python', '-m', module + '.migrate')
        start('auth', 'vatican-shop:mock-verify-20261117', envfile('auth', {'TEST_USER': user, 'TEST_OTHER': other}),
              ('python', '-c', AUTH))
        game = start('game', 'vatican-game:mock-verify-20261117', settings['game'], published=True)
        inventory = start('inventory', 'vatican-inventory:mock-verify-20261117', settings['inventory'], published=True)
        shop = start('shop', 'vatican-shop:mock-verify-20261117', settings['shop'], published=True)
        with httpx.Client(timeout=5, trust_env=False) as client:
            for base in (game, inventory, shop):
                wait(lambda: client.get(base + '/health/ready').status_code == 200, 'API ready')
            plan = {'schemaVersion': 1, 'bundleId': str(bundle), 'items': [
                {'itemIndex': 0, 'rewardType': 'WEAPON', 'rewardRef': 'sword.rare', 'quantity': 2}]}
            publish = '''import json,sys
from vatican_backend.application.level_rewards import PublishRewardBundle
from vatican_backend.domain.level_rewards import RewardPlan
from vatican_backend.persistence.postgres.database import Database,environment_url
from vatican_backend.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
db=Database(environment_url());PublishRewardBundle(PostgresUnitOfWorkFactory(db)).execute('docker_mock',1,RewardPlan.parse(json.load(sys.stdin)));db.close()
'''
            docker('exec', '-i', prefix + '-game', 'python', '-c', publish, data=json.dumps(plan))
            admin = {'Authorization': 'Bearer admin', 'X-Mock-Control-Key': keys['control']}
            player = {'Authorization': 'Bearer user'}

            def post(path, body, headers=admin, expected=200):
                response = client.post(shop + path, json=body, headers=headers)
                assert response.status_code == expected, (path, response.status_code)
                return response.json()

            def inspect(identity):
                response = client.get(shop + '/v1/mock/orders/' + identity, headers=player)
                response.raise_for_status()
                return response.json()

            def simulate(identity, scenario='SUCCESS', unavailable=False):
                return post('/v1/admin/shop/mock/orders/' + identity + '/simulate',
                    {'requestKey': str(uuid4()), 'scenario': scenario, 'providerUnavailable': unavailable})

            def hook(signal):
                # Exactly the same canonical bytes as the merchant's TEST simulator.
                body = json.dumps(signal['webhook']['body'], sort_keys=True, separators=(',', ':'), ensure_ascii=True).encode()
                response = client.post(shop + '/v1/mock/provider/webhook', content=body,
                                       headers={'X-Mock-Signature': signal['webhook']['signature']})
                assert response.status_code == 200
                return response.json()

            product = post('/v1/admin/shop/mock/products', {'sku': 'test.docker', 'version': 1,
                'externalSku': 'test.docker.v1', 'rewardBundleId': str(bundle), 'priceMinor': 199,
                'currencyIso': 'USD'}, expected=201)
            body = {'productId': product['productId'], 'requestKey': str(uuid4())}
            with ThreadPoolExecutor(max_workers=8) as pool:
                orders = list(pool.map(lambda _: post('/v1/mock/orders', body, player, 202), range(24)))
            first = orders[0]['orderId']
            assert len({order['orderId'] for order in orders}) == 1
            signal = simulate(first)
            with ThreadPoolExecutor(max_workers=8) as pool:
                replies = list(pool.map(lambda _: hook(signal), range(20)))
            assert sum(not reply['replayed'] for reply in replies) == 1
            worker = start('worker', 'vatican-shop:mock-verify-20261117', settings['shop'],
                           ('python', '-m', 'vatican_shop.worker'))
            wait(lambda: inspect(first)['status'] == 'FULFILLED', 'first fulfillment')
            assert len({unit['receipt']['instanceId'] for unit in inspect(first)['units']}) == 2
            assert post('/v1/mock/orders', body, player, 202)['replayed']

            second = post('/v1/mock/orders', {**body, 'requestKey': str(uuid4())}, player, 202)['orderId']
            timeout = simulate(second, 'TIMEOUT', True)
            assert timeout['receipt'] is None
            hook(timeout)
            wait(lambda: inspect(second)['status'] == 'UNKNOWN', 'provider unavailable')
            docker('kill', prefix + '-inventory')
            hook(simulate(second))
            wait(lambda: any(unit['attempts'] for unit in inspect(second)['units']), 'delivery during inventory outage')
            docker('kill', worker)
            docker('start', prefix + '-inventory')
            docker('exec', pg, 'psql', '-U', 'mock_test', '-d', 'shop', '-c',
                   "UPDATE mock_fulfillment_jobs SET available_at=now() WHERE state='PENDING';")
            docker('start', worker)
            wait(lambda: inspect(second)['status'] == 'FULFILLED', 'restart recovery')
            before = inspect(second)['units']
            for _ in range(20):
                hook(timeout)
            wait(lambda: inspect(second)['status'] == 'FULFILLED', 'late webhook reconciliation')
            assert inspect(second)['units'] == before

            refund = simulate(first, 'REFUND')
            hook(refund)
            wait(lambda: inspect(first)['status'] == 'REFUNDED', 'refund after fulfillment')
            assert inspect(first)['fulfillmentStatus'] == 'REVIEW'
            docker('stop', '-t', '3', worker)
            third = post('/v1/mock/orders', {**body, 'requestKey': str(uuid4())}, player, 202)['orderId']
            old = simulate(third)
            hook(simulate(third, 'REFUND'))
            hook(old)
            docker('start', worker)
            wait(lambda: inspect(third)['status'] == 'REFUNDED', 'refund before dispatch')
            assert inspect(third)['fulfillmentStatus'] == 'CANCELLED'
            assert client.get(shop + '/v1/mock/orders/' + second, headers={'Authorization': 'Bearer other'}).status_code == 404
            path = '/v1/admin/shop/mock/orders/' + second + '/simulate'
            for headers, status in ((player, 403), ({**admin, 'Authorization': 'Bearer guest'}, 403),
                                     ({**admin, 'X-Mock-Control-Key': 'invalid'}, 401)):
                response = client.post(shop + path, headers=headers, json={'requestKey': str(uuid4()), 'scenario': 'SUCCESS'})
                assert response.status_code == status
            rare = docker('exec', pg, 'psql', '-At', '-U', 'mock_test', '-d', 'inventory', '-c',
                          "SELECT count(*) FROM owned_weapons WHERE configuration_id='sword.rare';")
            receipts = docker('exec', pg, 'psql', '-At', '-U', 'mock_test', '-d', 'inventory', '-c',
                              'SELECT count(*) FROM mock_payment_receipts;')
            transactions = docker('exec', pg, 'psql', '-At', '-U', 'mock_test', '-d', 'shop', '-c',
                                  'SELECT count(*) FROM purchase_transactions;')
            assert (rare, receipts, transactions) == ('4', '4', '3')
            print(json.dumps({'result': 'passed', 'parallelCheckouts': 24, 'duplicateWebhooks': 20,
                'lateWebhooks': 20, 'orders': 3, 'transactions': 3, 'grantedRareWeapons': 4,
                'inventoryReceipts': 4, 'faults': ['provider unavailable', 'inventory killed', 'worker killed',
                'refund before dispatch', 'refund after dispatch'], 'transport': 'real HTTP',
                'auth': 'isolated local stub', 'database': 'ephemeral PostgreSQL 16'}, indent=2))
    finally:
        for name in reversed(names):
            subprocess.run(['docker', 'rm', '-fv', name], capture_output=True, timeout=30)
        subprocess.run(['docker', 'network', 'rm', network], capture_output=True, timeout=30)
        temporary.cleanup()


if __name__ == '__main__':
    main()
