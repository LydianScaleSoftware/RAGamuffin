# PaddleOCR (PP-StructureV3, layout detection, orientation) on CPU, for the layout comparison.
FROM python:3.11-slim
RUN apt-get update && apt-get install -y --no-install-recommends libgl1 libglib2.0-0 libgomp1 && rm -rf /var/lib/apt/lists/*
RUN pip install --no-cache-dir paddlepaddle==3.2.0 -i https://www.paddlepaddle.org.cn/packages/stable/cpu/ \
 && pip install --no-cache-dir "paddleocr[doc-parser]"
WORKDIR /work
CMD ["sleep", "infinity"]
