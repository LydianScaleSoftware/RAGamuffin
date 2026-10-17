# Probe how PP-StructureV3 reports tables: cell boxes vs the cells in its HTML.
import json, re, sys
from paddleocr import PPStructureV3
pipe = PPStructureV3(use_doc_orientation_classify=True, use_doc_unwarping=False, use_textline_orientation=True, device="cpu")
res = list(pipe.predict(sys.argv[1]))[0]
j = res.json["res"]
print("table_res_list:", len(j.get("table_res_list", [])))
for t in j.get("table_res_list", [])[:3]:
    print(" keys:", list(t.keys()))
    html = t.get("pred_html", "")
    tds = re.findall(r"<td[^>]*>", html)
    print(" cells in html:", len(tds), "cell_box_list:", len(t.get("cell_box_list", [])))
    print(" first boxes:", [[round(v) for v in b] for b in t.get("cell_box_list", [])[:4]])
    print(" html start:", html[:300])
    if "table_region_id" in t: print(" region id:", t["table_region_id"])
print("table blocks:", [(b["block_id"], [round(v) for v in b["block_bbox"]]) for b in j["parsing_res_list"] if b["block_label"] == "table"])
