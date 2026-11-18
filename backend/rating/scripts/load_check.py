"""Run destructive fault/load checks only against containers created by this process."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import sys
import time


def free_port():
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        return listener.getsockname()[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runs', type=int, default=1000)
    parser.add_argument('--clients', type=int, default=32)
    parser.add_argument('--report', type=Path, default=Path('backend/docs/rating/verification/load-result.json'))
    args = parser.parse_args()
    if sys.version_info < (3, 12) or not 64 <= args.runs <= 100000 or not 4 <= args.clients <= 128:
        parser.error('Requires Python 3.12+, runs=64..100000 and clients=4..128')
    root = Path(__file__).resolve().parents[3]
    report = args.report.resolve()
    report.parent.mkdir(parents=True, exist_ok=True)
    suffix, password = secrets.token_hex(6), secrets.token_hex(24)
    pg_name = 'vatican-rating-load-pg-' + suffix
    rabbit_name = 'vatican-rating-amqp-rabbit-load-' + suffix
    pg_port, rabbit_port = free_port(), free_port()
    while rabbit_port == pg_port:
        rabbit_port = free_port()
    created = []

    def run_container(name, image, variables, port):
        command = ['docker', 'run', '-d', '--name', name, '--label', 'vatican.rating.load-test=' + suffix]
        for key in variables:
            command += ['-e', key]
        command += ['-p', port, image]
        subprocess.run(command, env={**os.environ, **variables}, check=True, stdout=subprocess.DEVNULL)
        created.append(name)

    try:
        with ThreadPoolExecutor(max_workers=2) as pool:
            jobs = [pool.submit(run_container, pg_name, 'postgres:16', {
                'POSTGRES_USER': 'rating_test', 'POSTGRES_DB': 'rating_test', 'POSTGRES_PASSWORD': password,
            }, f'127.0.0.1:{pg_port}:5432'), pool.submit(run_container, rabbit_name, 'rabbitmq:4.1-management', {
                'RABBITMQ_DEFAULT_USER': 'rating_test', 'RABBITMQ_DEFAULT_PASS': password,
                'RABBITMQ_DEFAULT_VHOST': 'rating_test',
            }, f'127.0.0.1:{rabbit_port}:5672')]
            for job in jobs:
                job.result()
        import psycopg
        url = f'postgresql://rating_test:{password}@127.0.0.1:{pg_port}/rating_test'
        deadline = time.monotonic() + 45
        while True:
            try:
                with psycopg.connect(url, connect_timeout=1) as connection:
                    connection.execute('SELECT 1')
                break
            except psycopg.Error:
                if time.monotonic() >= deadline:
                    raise RuntimeError('Disposable PostgreSQL did not become ready') from None
                time.sleep(.2)
        env = {**os.environ, 'VD_TEST_DATABASE_URL': url,
            'VD_TEST_RABBITMQ_URL': f'amqp://rating_test:{password}@127.0.0.1:{rabbit_port}/rating_test',
            'VD_TEST_RABBITMQ_CONTAINER': rabbit_name, 'VD_TEST_RATING_POSTGRES_CONTAINER': pg_name,
            'VD_RATING_LOAD_OWNER': suffix, 'VD_RATING_LOAD_RUNS': str(args.runs),
            'VD_RATING_LOAD_CLIENTS': str(args.clients), 'VD_RATING_LOAD_REPORT': str(report)}
        return subprocess.call([sys.executable, '-m', 'pytest', '--import-mode=importlib', '-q', '-s',
            'backend/rating/tests/test_delivery_load.py', '--tb=short'], cwd=root, env=env)
    finally:
        for name in created:
            # Names came from successful docker run calls, never from the caller's environment.
            subprocess.run(['docker', 'rm', '-f', '-v', name], stdout=subprocess.DEVNULL, timeout=45)


if __name__ == '__main__':
    raise SystemExit(main())
