FROM python:3.12-slim
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
WORKDIR /srv
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY app ./app
RUN useradd -r app && chown -R app /srv
USER app
# WEB_CONCURRENCY = gunicorn workers (each async, handles many in-flight LLM calls; each has its own DB pool).
# Use 1 on tiny free instances (512 MB / 0.1 vCPU). PORT is set by most PaaS hosts (Koyeb, Render).
ENV WEB_CONCURRENCY=4 PORT=8000
EXPOSE 8000
CMD gunicorn app.main:app -k uvicorn.workers.UvicornWorker -b 0.0.0.0:${PORT} --timeout 120 --keep-alive 30
