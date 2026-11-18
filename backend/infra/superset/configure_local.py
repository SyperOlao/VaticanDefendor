"""Add missing local BI secrets without printing or replacing existing credentials."""
from pathlib import Path
import os
import secrets


def main():
    infra = Path(__file__).resolve().parents[1]
    path = infra / '.env'
    fresh = not path.exists()
    body = (infra / '.env.example').read_text() if fresh else path.read_text()
    lines = body.splitlines()
    generated = {
        'VD_CLICKHOUSE_PASSWORD', 'VD_SUPERSET_SECRET_KEY', 'VD_SUPERSET_METADATA_DB_PASSWORD',
        'VD_SUPERSET_ADMIN_PASSWORD', 'VD_SUPERSET_CLICKHOUSE_PASSWORD',
    }
    present = set()
    updated = []
    for index, line in enumerate(lines):
        if '=' not in line or line.lstrip().startswith('#'):
            continue
        key, value = line.split('=', 1)
        key = key.strip()
        present.add(key)
        if key in generated and (not value.strip() or fresh and 'replace-with-' in value):
            lines[index] = key + '=' + secrets.token_urlsafe(48)
            updated.append(key)
    for key in sorted(generated - present):
        lines.append(key + '=' + secrets.token_urlsafe(48))
        updated.append(key)
    if fresh or updated:
        path.write_text('\n'.join(lines) + '\n')
    os.chmod(path, 0o600)
    print('Local BI environment ready. Existing credentials preserved.')
    if updated:
        print('Generated keys: ' + ', '.join(updated))
    print('Login: VD_SUPERSET_ADMIN_USERNAME (default admin); password: VD_SUPERSET_ADMIN_PASSWORD in backend/infra/.env')


if __name__ == '__main__':
    main()
