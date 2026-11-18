import argparse
import asyncio

from ..application.access import BootstrapAdminUseCase
from ..application.catalog import BOOTSTRAP_ROLE_KEY, ROLE_MAP
from ..bootstrap.di import create_auth_container
from ..bootstrap.settings import AuthSettings


async def bootstrap(login: str) -> None:
    container = create_auth_container(AuthSettings.from_environment())
    try:
        async with container() as operation:
            account_id = await (await operation.get(BootstrapAdminUseCase)).execute(login)
            print(f"{ROLE_MAP[BOOTSTRAP_ROLE_KEY].code} assigned to account {account_id}")
    finally:
        await container.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=f"Assign {ROLE_MAP[BOOTSTRAP_ROLE_KEY].code} to an existing registered account")
    parser.add_argument("--login", required=True)
    asyncio.run(bootstrap(parser.parse_args().login))
