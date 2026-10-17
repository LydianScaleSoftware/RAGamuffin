# Probe PP-StructureV3's result structure on one page.
import json, sys, time
from paddleocr import PPStructureV3
t = time.time()
pipe = PPStructureV3(use_doc_orientation_classify=True, use_doc_unwarping=False, use_textline_orientation=True, device="cpu")
print(f"load {time.time()-t:.1f}s", flush=True)
t = time.time()
res = list(pipe.predict(sys.argv[1]))[0]
print(f"predict {time.time()-t:.1f}s")
j = res.json["res"] if "res" in res.json else res.json
print("keys:", list(j.keys()))
pre = j.get("doc_preprocessor_res", {})
print("preprocessor:", {k: v for k, v in pre.items() if k != "output_img"})
blocks = j.get("parsing_res_list", [])
print(len(blocks), "blocks; first block keys:", list(blocks[0].keys()) if blocks else None)
for b in blocks[:40]:
    print(f"  [{b.get('block_order')}] {b.get('block_label'):14} {[round(x) for x in b.get('block_bbox', [])]} {repr(b.get('block_content',''))[:90]}")
