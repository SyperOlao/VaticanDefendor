from clickhouse_sqlalchemy import get_declarative_base
from sqlalchemy import MetaData

# A separate registry, metadata and dialect. Never import PostgreSQL tables here.
Base = get_declarative_base(metadata=MetaData())
