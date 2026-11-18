FROM python:3.12-slim
WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY vatican_backend ./vatican_backend
COPY profile-avatars.json ./profile-avatars.json
COPY prestige-example.json ./prestige-example.json
COPY local-run-example.json ./local-run-example.json
COPY alembic ./alembic
COPY alembic.ini .
COPY weapon-catalog.json .
COPY progression-example.json .
COPY level-reward-bundle-example.json .
COPY level-reward-effect-example.json .
COPY case-example.json .
COPY media ./media
USER 65532:65532
HEALTHCHECK --interval=5s --timeout=3s --retries=20 CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8080/health/ready', timeout=2)"
CMD ["python", "-m", "vatican_backend"]
