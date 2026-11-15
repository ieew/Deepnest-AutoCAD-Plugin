"""只读解析 DXF：检查排版结果里 INSERT 的实际落位 vs 板料框。"""
import sys, math, collections

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

path = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\dongw\Desktop\1.dxf"

with open(path, "r", encoding="utf-8", errors="ignore") as f:
    raw = [ln.rstrip("\r\n") for ln in f]

# 组装 (code, value)
pairs = []
i = 0
while i + 1 < len(raw):
    code = raw[i].strip()
    val = raw[i + 1]
    i += 2
    try:
        code = int(code)
    except ValueError:
        continue
    pairs.append((code, val.strip()))

# 切分 section
sections = {}
cur = None
buf = []
for code, val in pairs:
    if code == 0 and val == "SECTION":
        buf = []
        continue
    if code == 2 and buf == [] and cur is None:
        cur = val
        continue
    if code == 0 and val == "ENDSEC":
        sections[cur] = buf
        cur = None
        buf = []
        continue
    if cur is not None:
        buf.append((code, val))

print("sections:", list(sections.keys()))

def split_entities(records):
    """按 code==0 切分成实体字典列表。"""
    out = []
    entity = None
    for code, val in records:
        if code == 0:
            if entity is not None:
                out.append(entity)
            entity = {"type": val, "groups": []}
        elif entity is not None:
            entity["groups"].append((code, val))
    if entity is not None:
        out.append(entity)
    return out

def g(entity, code, default=None):
    for c, v in entity["groups"]:
        if c == code:
            return v
    return default

def points_of(entity):
    xs = [float(v) for c, v in entity["groups"] if c == 10]
    ys = [float(v) for c, v in entity["groups"] if c == 20]
    return list(zip(xs, ys))

def bbox_of_entities(entities):
    xs, ys = [], []
    for e in entities:
        t = e["type"]
        if t in ("LINE",):
            pts = points_of(e)
            # LINE 有 10/20 与 11/21；points_of 只取 10/20，补 11/21
            x11 = [float(v) for c, v in e["groups"] if c == 11]
            y21 = [float(v) for c, v in e["groups"] if c == 21]
            pts += list(zip(x11, y21))
        elif t in ("LWPOLYLINE", "POLYLINE", "SPLINE", "ELLIPSE"):
            pts = points_of(e)
        elif t in ("CIRCLE", "ARC"):
            cx = float(g(e, 10, 0)); cy = float(g(e, 20, 0)); r = float(g(e, 40, 0))
            pts = [(cx - r, cy - r), (cx + r, cy + r)]
        elif t in ("TEXT", "MTEXT", "ATTDEF", "ATTRIB"):
            pts = points_of(e) or []
        else:
            pts = points_of(e)
        for x, y in pts:
            xs.append(x); ys.append(y)
    if not xs:
        return None
    return (min(xs), min(ys), max(xs), max(ys))

# --- BLOCKS ---
blocks = {}
if "BLOCKS" in sections:
    cur_block = None
    ents = []
    for e in split_entities(sections["BLOCKS"]):
        if e["type"] == "BLOCK":
            name = g(e, 2, "?")
            bx = float(g(e, 10, 0)); by = float(g(e, 20, 0))
            cur_block = (name, bx, by)
            ents = []
        elif e["type"] == "ENDBLK":
            if cur_block:
                blocks[cur_block[0]] = {"base": (cur_block[1], cur_block[2]), "bbox": bbox_of_entities(ents), "n": len(ents)}
            cur_block = None
            ents = []
        elif cur_block is not None:
            ents.append(e)

print("blocks:", len(blocks))

# --- ENTITIES ---
inserts = []
sheets = []
for e in split_entities(sections.get("ENTITIES", [])):
    t = e["type"]
    if t == "INSERT":
        name = g(e, 2, "?")
        ins = (float(g(e, 10, 0)), float(g(e, 20, 0)))
        rot = float(g(e, 50, 0) or 0)
        sx = float(g(e, 41, 1) or 1); sy = float(g(e, 42, 1) or 1)
        layer = g(e, 8, "0")

        # XData：按 1001(应用名) 分组收集 1000 字符串
        apps = {}
        cur_app = None
        for c, v in e["groups"]:
            if c == 1001:
                cur_app = v
                apps[cur_app] = []
            elif c == 1000 and cur_app:
                apps[cur_app].append(v)

        inserts.append({"name": name, "ins": ins, "rot": rot, "s": (sx, sy),
                        "layer": layer, "apps": apps,
                        "xdata": (apps.get("DEEPNEST") or [None])[0]})
    elif t == "LWPOLYLINE":
        layer = g(e, 8, "0")
        pts = points_of(e)
        if pts:
            sheets.append({"layer": layer, "bbox": (min(p[0] for p in pts), min(p[1] for p in pts),
                                                    max(p[0] for p in pts), max(p[1] for p in pts))})

sheet_frames = [s for s in sheets if s["layer"].upper() == "DEEPNEST_SHEET"]
print("INSERT 总数:", len(inserts))
print("DEEPNEST_SHEET 多段线:", len(sheet_frames))
for s in sheet_frames[:6]:
    print("  sheet bbox:", [round(v, 2) for v in s["bbox"]])
if inserts:
    layers = collections.Counter(i["layer"] for i in inserts)
    print("INSERT 图层分布:", layers.most_common(6))

def transform_point(p, ins, rot_deg, s, base):
    x = (p[0] - base[0]) * s[0]
    y = (p[1] - base[1]) * s[1]
    r = math.radians(rot_deg)
    return (ins[0] + x * math.cos(r) - y * math.sin(r),
            ins[1] + x * math.sin(r) + y * math.cos(r))

def world_bbox(ins):
    b = blocks.get(ins["name"])
    if not b or not b["bbox"]:
        return None
    bb = b["bbox"]
    corners = [(bb[0], bb[1]), (bb[2], bb[1]), (bb[2], bb[3]), (bb[0], bb[3])]
    pts = [transform_point(c, ins["ins"], ins["rot"], ins["s"], b["base"]) for c in corners]
    return (min(p[0] for p in pts), min(p[1] for p in pts), max(p[0] for p in pts), max(p[1] for p in pts))

def inside(inner, outer):
    return inner[0] >= outer[0] - 1e-6 and inner[1] >= outer[1] - 1e-6 and inner[2] <= outer[2] + 1e-6 and inner[3] <= outer[3] + 1e-6

ok = 0; bad = 0; unknown = 0
bad_samples = []
for ins in inserts:
    wb = world_bbox(ins)
    if wb is None:
        unknown += 1
        continue
    if any(inside(wb, s["bbox"]) for s in sheet_frames):
        ok += 1
    else:
        bad += 1
        if len(bad_samples) < 6 and ins["xdata"]:
            bad_samples.append((ins, wb))

copies = [i for i in inserts if i["xdata"] and str(i["xdata"]).startswith("PART|")]
print(f"\n带 DEEPNEST XData 的副本: {len(copies)}")
copy_ok = 0; copy_bad = 0; copy_bad_samples = []
for ins in copies:
    wb = world_bbox(ins)
    if wb is None:
        continue
    if any(inside(wb, s["bbox"]) for s in sheet_frames):
        copy_ok += 1
    else:
        copy_bad += 1
        if len(copy_bad_samples) < 6:
            copy_bad_samples.append((ins, wb))

print(f"  副本在板料内: {copy_ok}；副本在板料外: {copy_bad}")
for ins, wb in copy_bad_samples:
    b = blocks.get(ins["name"], {})
    print("\n--- 越界副本 ---")
    print("  block:", ins["name"], "layer:", ins["layer"], "ins:", [round(v, 2) for v in ins["ins"]], "rot:", round(ins["rot"], 3))
    print("  xdata:", ins["xdata"])
    print("  block base:", [round(v, 2) for v in b.get("base", [])], "bbox:", [round(v, 2) for v in (b.get("bbox") or [])])
    print("  world bbox:", [round(v, 2) for v in wb])

print("\n--- 前 6 个副本 ---")
for ins in copies[:6]:
    wb = world_bbox(ins)
    print(" block", ins["name"][:20], "ins", [round(v, 2) for v in ins["ins"]], "rot", round(ins["rot"], 2), "world", [round(v, 2) for v in wb] if wb else None, "|", ins["xdata"])

print(f"\n全部 INSERT 统计：在板内 {ok}，板外 {bad}，无几何 {unknown}")
