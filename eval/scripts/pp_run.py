# Layout comparison: PP-StructureV3 on its own, and PP-StructureV3 layout + glm-ocr per region.
# Usage: python pp_run.py <pages dir> <out dir> <ollama url>
import base64, io, json, os, sys, time, urllib.request

import numpy as np
from PIL import Image
from paddleocr import PPStructureV3

pages_dir, out_dir, ollama = sys.argv[1], sys.argv[2], sys.argv[3].rstrip("/")
pp_dir, hybrid_dir = os.path.join(out_dir, "paddle"), os.path.join(out_dir, "hybrid")
os.makedirs(pp_dir, exist_ok=True)
os.makedirs(hybrid_dir, exist_ok=True)

# Regions that are page furniture, not content.
SKIP = {"header", "footer", "number", "header_image", "footer_image", "aside_text", "seal"}
HEADINGS = {"doc_title": "# ", "paragraph_title": "## "}

pipe = PPStructureV3(use_doc_orientation_classify=True, use_doc_unwarping=False, use_textline_orientation=True, device="cpu")


def glm(image, prompt):
    buf = io.BytesIO()
    image.save(buf, format="PNG")
    body = {
        "model": "glm-ocr", "stream": False, "keep_alive": "5m",
        "options": {"temperature": 0, "num_ctx": 16384},
        "messages": [{"role": "user", "content": prompt, "images": [base64.b64encode(buf.getvalue()).decode()]}],
    }
    req = urllib.request.Request(f"{ollama}/api/chat", json.dumps(body).encode(), {"Content-Type": "application/json"})
    text = json.load(urllib.request.urlopen(req, timeout=300))["message"]["content"].strip()
    if text.startswith("```"):
        first, last = text.find("\n"), text.rfind("```")
        text = text[first + 1:last].strip() if last > first > 0 else text
    return text


for name in sorted(f for f in os.listdir(pages_dir) if f.endswith(".png")):
    label = name[:-4]
    t0 = time.time()
    res = list(pipe.predict(os.path.join(pages_dir, name)))[0]
    t_pp = time.time() - t0

    # 1. PP-StructureV3 on its own.
    md = res.markdown
    text = md.get("markdown_texts", "") if isinstance(md, dict) else str(md)
    open(os.path.join(pp_dir, label + ".md"), "w").write(text)
    j = res.json["res"] if "res" in res.json else res.json
    angle = (j.get("doc_preprocessor_res") or {}).get("angle", 0)
    open(os.path.join(pp_dir, label + ".meta"), "w").write(f"PP-StructureV3 (rotated {angle}°)|{t_pp:.1f}")

    # 2. Hybrid: PP layout on the upright image, glm-ocr for each content region.
    upright = res["doc_preprocessor_res"]["output_img"] if res["doc_preprocessor_res"] else None
    image = Image.fromarray(np.asarray(upright)[:, :, ::-1]) if upright is not None else Image.open(os.path.join(pages_dir, name)).convert("RGB")
    image.save(os.path.join(hybrid_dir, label + ".upright.png"))
    t1 = time.time()
    parts, regions = [], []
    for b in j["parsing_res_list"]:
        kind = b["block_label"]
        bbox = [int(v) for v in b["block_bbox"]]
        regions.append({"label": kind, "bbox": bbox})
        if kind in SKIP:
            continue
        if kind in ("image", "chart"):
            parts.append("<!-- figure -->")
            continue
        pad = 6
        crop = image.crop((max(0, bbox[0] - pad), max(0, bbox[1] - pad), min(image.width, bbox[2] + pad), min(image.height, bbox[3] + pad)))
        prompt = "Table Recognition:" if kind == "table" else "Formula Recognition:" if kind == "formula" else "Text Recognition:"
        content = glm(crop, prompt)
        if kind in HEADINGS:
            content = HEADINGS[kind] + " ".join(content.split())
        elif kind == "figure_title":
            content = "*" + " ".join(content.split()) + "*"
        parts.append(content)
    t_glm = time.time() - t1
    open(os.path.join(hybrid_dir, label + ".md"), "w").write("\n\n".join(parts))
    json.dump(regions, open(os.path.join(hybrid_dir, label + ".regions.json"), "w"))
    open(os.path.join(hybrid_dir, label + ".meta"), "w").write(
        f"PP layout (rotated {angle}°) + glm-ocr per region|{t_pp + t_glm:.1f}")
    print(f"{label}: angle {angle}, {len(regions)} regions, PP {t_pp:.1f}s, glm {t_glm:.1f}s", flush=True)
