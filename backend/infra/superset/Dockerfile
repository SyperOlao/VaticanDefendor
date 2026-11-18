FROM apache/superset:6.0.0
USER root
COPY requirements.txt /app/vatican-requirements.txt
RUN uv pip install --python /app/.venv/bin/python --no-cache -r /app/vatican-requirements.txt
COPY --chown=superset:superset . /app/vatican
USER superset
ENV SUPERSET_CONFIG_PATH=/app/vatican/superset_config.py
ENV PYTHONPATH=/app/vatican:${PYTHONPATH}
