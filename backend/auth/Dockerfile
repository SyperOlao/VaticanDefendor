FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1

WORKDIR /app
COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt \
    && useradd --uid 10001 --user-group --create-home --shell /usr/sbin/nologin auth

COPY vatican_auth ./vatican_auth
COPY alembic ./alembic
COPY alembic.ini ./alembic.ini

USER auth
EXPOSE 8080
HEALTHCHECK --interval=5s --timeout=3s --start-period=10s --retries=10 \
    CMD ["python", "-c", "import os, urllib.request; urllib.request.urlopen('http://127.0.0.1:' + os.environ.get('VD_AUTH_PORT', '8080') + '/health/ready', timeout=2)"]
CMD ["python", "-m", "vatican_auth"]
