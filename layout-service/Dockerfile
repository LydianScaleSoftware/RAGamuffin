# Layout analysis service for RagServer: PaddleOCR PP-StructureV3 behind a small HTTP API.
FROM python:3.11-slim
RUN apt-get update && apt-get install -y --no-install-recommends libgl1 libglib2.0-0 libgomp1 curl && rm -rf /var/lib/apt/lists/*
RUN pip install --no-cache-dir paddlepaddle==3.2.0 -i https://www.paddlepaddle.org.cn/packages/stable/cpu/ \
 && pip install --no-cache-dir "paddleocr[doc-parser]==3.7.0" fastapi==0.115.12 "uvicorn[standard]==0.34.2" python-multipart==0.0.20
WORKDIR /app
COPY app.py .
EXPOSE 5002
HEALTHCHECK --interval=30s --timeout=5s --start-period=600s CMD curl -fs http://localhost:5002/health | grep -q '"ok"' || exit 1
CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "5002", "--workers", "1"]
