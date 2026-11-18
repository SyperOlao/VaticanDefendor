"""Local Superset metadata/cache configuration; business data stays in ClickHouse."""
import os
from urllib.parse import quote

SECRET_KEY = os.environ['SUPERSET_SECRET_KEY']
SQLALCHEMY_DATABASE_URI = (
    'postgresql+psycopg2://superset:' + quote(os.environ['SUPERSET_METADATA_PASSWORD'], safe='')
    + '@' + os.getenv('SUPERSET_METADATA_HOST', 'superset_metadata') + ':5432/superset'
)
SQLALCHEMY_TRACK_MODIFICATIONS = False
WTF_CSRF_ENABLED = True
SESSION_COOKIE_HTTPONLY = True
SESSION_COOKIE_SAMESITE = 'Lax'
SESSION_COOKIE_SECURE = False  # Localhost HTTP; no externally published listener.
AUTH_USER_REGISTRATION = False
SUPERSET_WEBSERVER_TIMEOUT = 90
SQLLAB_TIMEOUT = 30
ROW_LIMIT = 10000
SAMPLES_ROW_LIMIT = 1000
ENABLE_PROXY_FIX = False
APP_NAME = 'VaticanDefendor · Analytics'
BABEL_DEFAULT_LOCALE = 'ru'
LANGUAGES = {'ru': {'flag': 'ru', 'name': 'Русский'}, 'en': {'flag': 'us', 'name': 'English'}}

_cache = {
    'CACHE_TYPE': 'RedisCache',
    'CACHE_DEFAULT_TIMEOUT': 60,
    'CACHE_REDIS_URL': 'redis://' + os.getenv('SUPERSET_CACHE_HOST', 'superset_cache') + ':6379/0',
}
CACHE_CONFIG = {**_cache, 'CACHE_KEY_PREFIX': 'vatican_meta_'}
DATA_CACHE_CONFIG = {**_cache, 'CACHE_KEY_PREFIX': 'vatican_data_'}
FILTER_STATE_CACHE_CONFIG = {**_cache, 'CACHE_KEY_PREFIX': 'vatican_filters_', 'CACHE_DEFAULT_TIMEOUT': 86400}
EXPLORE_FORM_DATA_CACHE_CONFIG = {**_cache, 'CACHE_KEY_PREFIX': 'vatican_explore_', 'CACHE_DEFAULT_TIMEOUT': 86400}
RATELIMIT_STORAGE_URI = 'redis://' + os.getenv('SUPERSET_CACHE_HOST', 'superset_cache') + ':6379/1'

from error_policy import install_chart_error_handling
FLASK_APP_MUTATOR = install_chart_error_handling
