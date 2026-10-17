# Builds eval/work/layout-comparison.html from the outputs in eval/work/out.
# Usage: python3 build_report.py <work dir>
import base64, html, os, re, subprocess, sys, tempfile

work = sys.argv[1]
out = os.path.join(work, "out")
METHODS = [
    ("current", "Current pipeline", "Embedded text via PdfPig, or glm-ocr on the whole page"),
    ("docling", "Docling (standard)", "Docling layout model + tables + its default OCR"),
    ("paddle", "PP-StructureV3", "Paddle orientation + layout + its own OCR"),
    ("hybrid", "PP layout + glm-ocr", "Paddle orientation + layout; glm-ocr reads each region"),
    ("new", "New RagServer pipeline", "Layout service + glm-ocr per region (scans) or the PDF's own words per region"),
]
PAGES = [
    ("A-toolbox1-p12", "Toolbox part 1, page 12", "Two-page spread scanned sideways; diagrams, photo, captions"),
    ("B-toolbox2-p40", "Toolbox part 2, page 40", "Two-page spread scanned sideways; tables and code"),
    ("C-datasheet-p7", "W65C816S data sheet, page 7", "Embedded text; running header and footer"),
    ("D-datasheet-p9", "W65C816S data sheet, page 9", "Embedded text; numbered sections"),
    ("E-opcodes-p1", "crow_cousins opcodes, page 1", "Embedded text; dense multi-column opcode table"),
]
TABLE_TAGS = re.compile(r"&lt;(/?)(table|thead|tbody|tr|td|th|br)((?:\s+[a-z-]+=&quot;[^&]*&quot;)*)\s*/?&gt;", re.I)
SPAN = re.compile(r"(colspan|rowspan)=&quot;(\d+)&quot;")


def render(text):
    """Escape everything, then re-enable plain table markup; mark headings and figure placeholders."""
    text = re.sub(r"<img[^>]*>", "[image]", text)
    text = re.sub(r"</?div[^>]*>|</?html>|</?body>", "", text)
    esc = html.escape(text)
    esc = TABLE_TAGS.sub(lambda m: "<" + m.group(1) + m.group(2) + "".join(f' {a}="{v}"' for a, v in SPAN.findall(m.group(3))) + ">", esc)
    esc = re.sub(r"^(#{1,6}) (.*)$", lambda m: f'<span class="h">{m.group(1)} {m.group(2)}</span>', esc, flags=re.M)
    esc = esc.replace("&lt;!-- image --&gt;", '<span class="fig">[figure]</span>').replace("&lt;!-- figure --&gt;", '<span class="fig">[figure]</span>')
    return esc


def thumbnail(path, width=900):
    """Downscaled JPEG as a data URI, so the report is a single self-contained file."""
    with tempfile.TemporaryDirectory() as tmp:
        dst = os.path.join(tmp, "t.jpg")
        subprocess.run(["sips", "-s", "format", "jpeg", "-s", "formatOptions", "70", "-Z", str(width), path, "--out", dst],
                       check=True, capture_output=True)
        return "data:image/jpeg;base64," + base64.b64encode(open(dst, "rb").read()).decode()


def read(path, default=""):
    return open(path).read() if os.path.exists(path) else default


sections = []
for key, title, note in PAGES:
    upright = os.path.join("out", "hybrid", key + ".upright.png")
    image = thumbnail(os.path.join(work, upright if os.path.exists(os.path.join(work, upright)) else os.path.join("pages", key + ".png")))
    cols = []
    for m, name, desc in METHODS:
        body = read(os.path.join(out, m, key + ".md"), "(no output)")
        meta = read(os.path.join(out, m, key + ".meta"), "|").split("|")
        detail, secs = (meta + ["", ""])[:2]
        cols.append(f"""
        <div class="col">
          <div class="colhead"><strong>{name}</strong><span class="muted">{html.escape(detail or desc)} · {secs}s · {len(body):,} chars</span></div>
          <div class="text">{render(body) if body.strip() else '<span class="empty">(no text)</span>'}</div>
        </div>""")
    sections.append(f"""
    <section id="{key}">
      <div class="pagehead">
        <img src="{image}" alt="{html.escape(title)}">
        <div><h2>{html.escape(title)}</h2><p class="muted">{html.escape(note)}</p>
        <p class="muted small">Image shows the page as the hybrid saw it (upright, for rotated scans).</p></div>
      </div>
      <div class="cols">{''.join(cols)}</div>
    </section>""")

nav = " · ".join(f'<a href="#{k}">{html.escape(t)}</a>' for k, t, _ in PAGES)
page = f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Layout comparison</title>
<style>
:root {{ --bg:#f6f7f9; --card:#fff; --text:#1c2024; --muted:#6b7280; --border:#e3e5e8; --accent:#2563eb; --code:#f3f4f6; }}
@media (prefers-color-scheme: dark) {{ :root {{ --bg:#111317; --card:#1a1d22; --text:#e6e8eb; --muted:#9aa1ab; --border:#2c3038; --accent:#4f8cff; --code:#0f1115; }} }}
* {{ box-sizing:border-box; }}
body {{ margin:0; background:var(--bg); color:var(--text); font:14px/1.5 -apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif; }}
header {{ padding:16px 24px; background:var(--card); border-bottom:1px solid var(--border); position:sticky; top:0; z-index:1; }}
header h1 {{ font-size:18px; margin:0 0 4px; }}
a {{ color:var(--accent); }}
.muted {{ color:var(--muted); }} .small {{ font-size:12px; }}
section {{ margin:20px 16px; background:var(--card); border:1px solid var(--border); border-radius:10px; padding:16px; }}
.pagehead {{ display:flex; gap:16px; align-items:flex-start; margin-bottom:12px; }}
.pagehead img {{ width:360px; max-width:100%; border:1px solid var(--border); border-radius:6px; }}
.pagehead h2 {{ margin:0 0 4px; font-size:16px; }}
.cols {{ display:grid; grid-template-columns:repeat(5, minmax(0,1fr)); gap:10px; }}
@media (max-width:1400px) {{ .cols {{ grid-template-columns:repeat(2, minmax(0,1fr)); }} }}
@media (max-width:640px) {{ .cols {{ grid-template-columns:1fr; }} .pagehead {{ flex-direction:column; }} }}
.col {{ border:1px solid var(--border); border-radius:8px; display:flex; flex-direction:column; min-width:0; }}
.colhead {{ padding:8px 10px; border-bottom:1px solid var(--border); display:flex; flex-direction:column; font-size:12px; }}
.text {{ padding:8px 10px; white-space:pre-wrap; word-break:break-word; font:12px/1.45 ui-monospace,SFMono-Regular,Menlo,monospace; max-height:640px; overflow:auto; background:var(--code); }}
.text table {{ border-collapse:collapse; white-space:normal; margin:4px 0; }}
.text td, .text th {{ border:1px solid var(--border); padding:2px 4px; vertical-align:top; }}
.h {{ font-weight:700; color:var(--accent); }} .fig {{ color:var(--muted); font-style:italic; }} .empty {{ color:var(--muted); }}
</style></head><body>
<header><h1>Layout analysis comparison</h1><div class="small">{nav}</div></header>
{''.join(sections)}
</body></html>"""
open(os.path.join(work, "layout-comparison.html"), "w").write(page)
print("wrote", os.path.join(work, "layout-comparison.html"))
