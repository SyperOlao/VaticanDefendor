from clickhouse_sqlalchemy import make_session
from sqlalchemy import create_engine
from sqlalchemy.engine import make_url


def environment_url():
    """Warehouse-only commands do not require inbox or service credentials."""
    import os
    from sqlalchemy import URL
    explicit = os.getenv('VD_ANALYTICS_DATABASE_URL')
    if explicit:
        return explicit
    return URL.create('clickhouse+http', host=os.getenv('VD_CLICKHOUSE_HOST', 'clickhouse'),
        port=int(os.getenv('VD_CLICKHOUSE_PORT', '8123')), database=os.getenv('VD_CLICKHOUSE_DB', 'vatican_analytics'),
        username=os.getenv('VD_CLICKHOUSE_USER', 'analytics'),
        password=os.environ['VD_CLICKHOUSE_PASSWORD']).render_as_string(hide_password=False)


class Database:
    def __init__(self, url: str):
        if make_url(url).drivername != "clickhouse+http":
            raise ValueError("VD_ANALYTICS_DATABASE_URL must use clickhouse+http://")
        self.engine = create_engine(url, connect_args={"timeout": 5})

    def session(self):
        return make_session(self.engine)

    def close(self) -> None:
        self.engine.dispose()
