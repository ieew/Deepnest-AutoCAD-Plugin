"""对比"原件 vs 副本"的插入点/旋转/包围盒，定位 TransformBy 实际行为。"""
import sys, math

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

path = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\dongw\Desktop\1.dxf"
raw = open(path, "r", encoding="utf-8", errors="ignore").read().splitlines()

pairs = []
i = 0
while i + 1 < len(raw):
    try:
        code = int(raw[i].strip())
    except ValueError:
        i += 2
        continue
    pairs.append((code, raw[i + 1].strip()))
    i += 2

sections, cur, buf = {}, None, []
for code, val in pairs:
    if code == 0 and val == "SECTION":
        buf = []
        continue
    if code == 2 and cur is None and not buf:
        cur = val
        continue
    if code == 0 and val == "ENDSEC":
        sections[cur] = buf
        cur, buf = None, []
        continue
    if cur is not None:
        buf.append((code, val))


def split_entities(records):
    out, entity = [], None
    for code, val in records:
        if code == 0:
            if entity:
                out.append(entity)
            entity = {"type": val, "groups": []}
        elif entity is not None:
            entity["groups"].append((code, val))
    if entity:
        out.append(entity)
    return out


def first(e, code, default=None):
    for c, v in e["groups"]:
        if c == code:
            return v
    return default


def allv(e, code):
    return [v for c, v in e["groups"] if c == code]


def pts(e):
    xs = [float(v) for v in allv(e, 10)]
    ys = [float(v) for v in allv(e, 20)]
    return list(zip(xs, ys))


def bbox(entities):
    xs, ys = [], []
    for e in entities:
        t = e["type"]
        if t == "LINE":
            p = pts(e) + list(zip([float(v) for v in allv(e, 11)], [float(v) for v in allv(e, 21)]))
        elif t in ("CIRCLE", "ARC"):
            cx, cy, r = float(first(e, 10, 0)), float(first(e, 20, 0)), float(first(e, 40, 0))
            p = [(cx - r, cy - r), (cx + r, cy + r)]
        else:
            p = pts(e)
        for x, y in p:
            xs.append(x); ys.append(y)
    return (min(xs), min(ys), max(xs), max(ys)) if xs else None


blocks = {}
if "BLOCKS" in sections:
    name = base = None
    ents = []
    for e in split_entities(sections["BLOCKS"]):
        if e["type"] == "BLOCK":
            name = first(e, 2, "?")
            base = (float(first(e, 10, 0)), float(first(e, 20, 0)))
            ents = []
        elif e["type"] == "ENDBLK":
            if name:
                blocks[name] = (base, bbox(ents))
            name, ents = None, []
        elif name:
            ents.append(e)

inserts = []
for e in split_entities(sections.get("ENTITIES", [])):
    if e["type"] != "INSERT":
        continue
    apps, app = {}, None
    for c, v in e["groups"]:
        if c == 1001:
            app = v
            apps[app] = []
        elif c == 1000 and app:
            apps[app].append(v)

    inserts.append({
        "handle": (first(e, 5, "") or "").upper(),
        "name": first(e, 2, "?"),
        "ins": (float(first(e, 10, 0)), float(first(e, 20, 0))),
        "rot": float(first(e, 50, 0) or 0),
        "scale": (float(first(e, 41, 1) or 1), float(first(e, 42, 1) or 1)),
        "payload": (apps.get("DEEPNEST") or [None])[0],
    })

by_handle = {i["handle"]: i for i in inserts if i["handle"]}
copies = [i for i in inserts if i["payload"] and i["payload"].startswith("PART|")]


def world_bbox(ins):
    b = blocks.get(ins["name"])
    if not b or not b[1]:
        return None
    base, bb = b
    corners = [(bb[0], bb[1]), (bb[2], bb[1]), (bb[2], bb[3]), (bb[0], bb[3])]
    r = math.radians(ins["rot"])
    out = []
    for cx, cy in corners:
        x = (cx - base[0]) * ins["scale"][0]
        y = (cy - base[1]) * ins["scale"][1]
        out.append((ins["ins"][0] + x * math.cos(r) - y * math.sin(r),
                    ins["ins"][1] + x * math.sin(r) + y * math.cos(r)))
    return (min(p[0] for p in out), min(p[1] for p in out),
            max(p[0] for p in out), max(p[1] for p in out))


print(f"INSERT={len(inserts)} 副本={len(copies)} 有 handle 的={len(by_handle)}")
shown = 0
for copy in copies:
    parts = copy["payload"].split("|")
    src = parts[1].upper() if len(parts) > 1 else None
    orig = by_handle.get(src)
    if not orig:
        continue

    ob = world_bbox(orig)
    cb = world_bbox(copy)
    if not ob or not cb:
        continue

    theta = float(parts[4])
    print("\n=== 副本 ===")
    print("  payload:", copy["payload"], " block:", copy["name"][:24])
    print("  原件: handle", orig["handle"], "ins", [round(v, 3) for v in orig["ins"]],
          "rot", round(orig["rot"], 3), "bbox", [round(v, 2) for v in ob])
    print("  副本: handle", copy["handle"], "ins", [round(v, 3) for v in copy["ins"]],
          "rot", round(copy["rot"], 3), "bbox", [round(v, 2) for v in cb])
    print("  期望 θ =", theta, "；实际 rot - orig.rot =", round(copy["rot"] - orig["rot"], 3))
    print("  bbox 位移 =", [round(cb[k] - ob[k], 2) for k in range(4)])
    print("  bbox 尺寸 原件/副本 =", round(ob[2] - ob[0], 2), round(ob[3] - ob[1], 2),
          "/", round(cb[2] - cb[0], 2), round(cb[3] - cb[1], 2))
    shown += 1
    if shown >= 4:
        break
