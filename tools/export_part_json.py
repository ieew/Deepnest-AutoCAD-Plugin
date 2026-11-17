"""把 DXF 里某个块的原始实体（LINE / LWPOLYLINE）导出为 JSON，供 tools/Probe 离线跑真实的 ContourBuilder。"""
import json
import os
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

DXF = sys.argv[1] if len(sys.argv) > 1 else "0.DXF"
TARGET = sys.argv[2] if len(sys.argv) > 2 else None
OUT = sys.argv[3] if len(sys.argv) > 3 else os.path.join(os.environ.get("TEMP", "."), "deepnest-part.json")

src = open(r"c:\Users\dongw\Desktop\新建文件夹\Deepnest\tools\dxf_loops.py", encoding="utf-8").read()
head = src.split('print(f"tol={TOL}')[0]
g1 = {"__name__": "loopsmod"}
_b = sys.argv
sys.argv = [sys.argv[0], DXF, "0.1"]
try:
    exec(compile(head, "dxf_loops_head", "exec"), g1)
finally:
    sys.argv = _b

blocks = g1["blocks"]
base_pts = g1["base_pts"]
split_entities = g1["split_entities"]
sections = g1["sections"]

if TARGET is None:
    inserts = [e for e in split_entities(sections.get("ENTITIES", [])) if e["type"] == "INSERT"]
    TARGET = inserts[0]["g"].get(2, [""])[0]

ents = blocks[TARGET]
entities = []
for e in ents:
    t = e["type"]
    if t == "LINE":
        entities.append({
            "type": "LINE",
            "a": [float(e["g"][10][0]), float(e["g"][20][0])],
            "b": [float(e["g"][11][0]), float(e["g"][21][0])],
        })
    elif t == "LWPOLYLINE":
        verts = g1["lwpoly_vertices"](e)
        entities.append({
            "type": "LWPOLYLINE",
            "closed": bool(int(e["g"].get(70, ["0"])[0]) & 1),
            "verts": verts,
        })
    else:
        entities.append({"type": t, "unsupported": True})

doc = {
    "block": TARGET,
    "baseTolerance": 0.1,
    "curveTolerance": 0.1,
    "entities": entities,
}

with open(OUT, "w", encoding="utf-8") as f:
    json.dump(doc, f, ensure_ascii=False, indent=1)

print(f"导出 {len(entities)} 个实体 -> {OUT}")
