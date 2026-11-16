FROM python:3.12-slim
WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY vatican_analytics ./vatican_analytics
CMD ["python", "-m", "vatican_analytics.init_db"]
