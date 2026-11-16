from clickhouse_sqlalchemy import make_session
from sqlalchemy import create_engine
from sqlalchemy.engine import make_url


class Database:
    def __init__(self, url: str):
        if make_url(url).drivername != "clickhouse+http":
            raise ValueError("VD_ANALYTICS_DATABASE_URL must use clickhouse+http://")
        self.engine = create_engine(url)

    def session(self):
        return make_session(self.engine)

    def close(self) -> None:
        self.engine.dispose()
