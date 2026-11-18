import os
from uuid import uuid4

import pytest
from sqlalchemy import create_engine
from sqlalchemy.engine import make_url

from vatican_auth.migrate import migrate


@pytest.fixture()
def storage():
    url = os.environ.get("VD_TEST_AUTH_DATABASE_URL")
    if not url:
        pytest.skip("Set VD_TEST_AUTH_DATABASE_URL to a test PostgreSQL database")
    url = make_url(url).set(drivername="postgresql+psycopg")
    admin = create_engine(url)
    schema = "auth_test_" + uuid4().hex
    engine = create_engine(url.update_query_dict({"options": f"-c search_path={schema} -c timezone=UTC"}))
    try:
        with admin.begin() as connection:
            connection.exec_driver_sql(f'CREATE SCHEMA "{schema}"')
        migrate(engine.url.render_as_string(hide_password=False))
        yield engine
    finally:
        engine.dispose()
        with admin.begin() as connection:
            connection.exec_driver_sql(f'DROP SCHEMA IF EXISTS "{schema}" CASCADE')
        admin.dispose()


