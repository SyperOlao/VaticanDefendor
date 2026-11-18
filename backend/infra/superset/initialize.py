"""Serialize metadata migration and provisioning across repeated Compose runs."""
import fcntl
from pathlib import Path
import subprocess


def main():
    with Path('/app/superset_home/vatican-init.lock').open('w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        for command in (
            ['/app/.venv/bin/superset', 'db', 'upgrade'],
            ['/app/.venv/bin/superset', 'init'],
            ['/app/.venv/bin/python', '/app/vatican/bootstrap.py'],
        ):
            subprocess.run(command, check=True)


if __name__ == '__main__':
    main()
