"""Page layout analysis for Ragamuffin, wrapping PaddleOCR's PP-StructureV3.

POST /analyze  (multipart field "image": one rendered page)
  -> {"angle", "width", "height", "upright_png" (base64, only when rotated), "regions": [...]}

Each region: {"label", "bbox": [x1, y1, x2, y2], "text", "table": {"html", "cells"} | null}, in reading order.
Coordinates refer to the upright image. "text" is Paddle's own reading of the region, which Ragamuffin uses only
as a fallback (it prefers glm-ocr for scans and the PDF's own words for digital pages).
"""
import base64
import gc
import io
import logging
import os
import threading
import time

import numpy as np
from fastapi import FastAPI, File, HTTPException, UploadFile
from PIL import Image

os.environ.setdefault("PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK", "True")
from paddleocr import PPStructureV3  # noqa: E402  (after the env var above)

log = logging.getLogger("layout")
logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")

app = FastAPI(title="RAGamuffin layout service")
_pipe = None
_lock = threading.Lock()  # one page at a time; the models are not thread-safe and the CPU is the bottleneck
_pages = 0  # pages analyzed since the models were loaded

# Paddle's predictors can get into a state where every call fails with "RuntimeError: std::exception" (seen after
# about 900 pages and 11 days, with the container's memory doubled), while /health still says ok. So a failed page
# reloads the models and is tried once more, and the models are reloaded every RECYCLE_PAGES pages anyway.
RECYCLE_PAGES = int(os.environ.get("LAYOUT_RECYCLE_PAGES", "500"))


def pipeline():
    global _pipe
    if _pipe is None:
        started = time.time()
        _pipe = PPStructureV3(
            use_doc_orientation_classify=True,
            use_doc_unwarping=False,
            use_textline_orientation=True,
            device=os.environ.get("LAYOUT_DEVICE", "cpu"),
        )
        log.info("PP-StructureV3 loaded in %.0fs", time.time() - started)
    return _pipe


def _reload(reason):
    global _pipe, _pages
    log.warning("Reloading PP-StructureV3: %s", reason)
    _pipe = None
    _pages = 0
    gc.collect()
    return pipeline()


def _predict(page):
    """Analyze one page; call with _lock held."""
    global _pages
    if RECYCLE_PAGES > 0 and _pages >= RECYCLE_PAGES:
        _reload(f"{_pages} pages since the last load")
    try:
        res = list(pipeline().predict(page))[0]
    except Exception as ex:
        log.exception("Analysis failed; retrying once with freshly loaded models")
        try:
            res = list(_reload(f"{type(ex).__name__}: {ex}").predict(page))[0]
        except Exception as retry_ex:
            # Fails on fresh models too, so it's this page. Reload again, so the next page doesn't inherit a broken state.
            _reload("the retry failed as well")
            raise HTTPException(status_code=500, detail=f"Layout analysis failed on this page: {type(retry_ex).__name__}: {retry_ex}")
    _pages += 1
    return res


def _load_models():
    with _lock:
        pipeline()


@app.on_event("startup")
def warm_up():
    # Load (and on first run download) the models before the first request arrives.
    threading.Thread(target=_load_models, daemon=True).start()


@app.get("/health")
def health():
    return {"status": "ok" if _pipe is not None else "loading", "engine": "pp-structurev3"}


# A plain (non-async) endpoint: FastAPI runs it in a worker thread, so /health keeps answering during analysis.
@app.post("/analyze")
def analyze(image: UploadFile = File(...)):
    data = image.file.read()
    try:
        # Paddle expects BGR, and on some pages it edits the image in place, so give it a writable copy.
        page = np.ascontiguousarray(np.array(Image.open(io.BytesIO(data)).convert("RGB"))[:, :, ::-1])
    except Exception as ex:
        raise HTTPException(status_code=400, detail=f"Not an image: {ex}")

    started = time.time()
    with _lock:
        res = _predict(page)
    j = res.json["res"] if "res" in res.json else res.json

    pre = j.get("doc_preprocessor_res") or {}
    angle = int(pre.get("angle", 0) or 0)
    upright = res["doc_preprocessor_res"]["output_img"] if angle and res["doc_preprocessor_res"] else None
    height, width = (upright.shape[:2] if upright is not None else page.shape[:2])

    # Tables in table_res_list appear in the same order as the table regions in parsing_res_list.
    tables = iter(j.get("table_res_list") or [])
    regions = []
    for b in j.get("parsing_res_list") or []:
        region = {
            "label": b["block_label"],
            "bbox": [round(float(v), 1) for v in b["block_bbox"]],
            "text": b.get("block_content") or "",
            "table": None,
        }
        if b["block_label"] == "table":
            t = next(tables, None)
            if t is not None:
                region["table"] = {
                    "html": t.get("pred_html", ""),
                    "cells": [[round(float(v), 1) for v in box] for box in t.get("cell_box_list", [])],
                }
        regions.append(region)

    upright_png = None
    if upright is not None:
        buf = io.BytesIO()
        Image.fromarray(np.asarray(upright)[:, :, ::-1]).save(buf, format="PNG")
        upright_png = base64.b64encode(buf.getvalue()).decode()

    log.info("analyzed %dx%d page: angle %d, %d regions in %.1fs", width, height, angle, len(regions), time.time() - started)
    return {"angle": angle, "width": int(width), "height": int(height), "upright_png": upright_png, "regions": regions}
