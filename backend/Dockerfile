FROM python:3.12-slim
WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY vatican_backend ./vatican_backend
COPY migrations ./migrations
COPY weapon-catalog.json ./weapon-catalog.json
COPY media ./media
CMD ["sh", "-c", "python -m vatican_backend.migrate && uvicorn vatican_backend.api:create_app --factory --host 0.0.0.0 --port 8080"]
