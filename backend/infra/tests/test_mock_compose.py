"""Validate the opt-in TEST stack without starting or changing any containers."""
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import subprocess

import pytest


@pytest.mark.skipif(shutil.which('docker') is None, reason='Docker Compose CLI is needed for config validation')
def test_mock_override_separates_explicit_volumes_even_with_copied_live_namespace():
    directory = Path(__file__).resolve().parents[1]
    base, override = directory / 'compose.yaml', directory / 'compose.mock.yaml'
    required = re.findall(r'\$\{(VD_[A-Z0-9_]+):\?', base.read_text() + override.read_text())
    env = {**os.environ, **{key: secrets.token_urlsafe(40) for key in required},
        'VD_DATA_NAMESPACE': 'ordinary-live-namespace', 'VD_DATA_EXTERNAL': 'true',
        'VD_MOCK_INSTANCE_ID': 'configuration-test'}
    command = ['docker', 'compose', '--env-file', '/dev/null', '-p', 'vatican-mock-config-test', '-f', str(base)]

    def config(arguments):
        result = subprocess.run([*arguments, 'config', '--format', 'json'], env=env,
                                capture_output=True, text=True, timeout=15)
        assert result.returncode == 0, 'Compose config validation failed'
        return json.loads(result.stdout)

    normal = config(command)
    assert 'VD_SHOP_MOCK_PAYMENTS_ENABLED' not in normal['services']['shop']['environment']
    merged = config([*command, '-f', str(override)])
    assert {v['name'] for v in normal['volumes'].values()}.isdisjoint(v['name'] for v in merged['volumes'].values())
    assert all(v['name'].startswith('vatican-mock-configuration-test_') and not v.get('external', False)
               for v in merged['volumes'].values())
    for service in ('shop', 'shop_worker'):
        settings = merged['services'][service]['environment']
        assert settings['VD_APP_ENVIRONMENT'] == 'test' and settings['VD_SHOP_MOCK_PAYMENTS_ENABLED'] == '1'
        assert settings['VD_SHOP_DB_HOST'] == 'shop_db'
    assert merged['services']['inventory']['environment']['VD_INVENTORY_MOCK_PAYMENTS_ENABLED'] == '1'
    env.pop('VD_MOCK_INSTANCE_ID')
    refused = subprocess.run([*command, '-f', str(override), 'config', '--quiet'], env=env,
                             capture_output=True, text=True, timeout=15)
    assert refused.returncode != 0 and 'VD_MOCK_INSTANCE_ID' in refused.stderr
