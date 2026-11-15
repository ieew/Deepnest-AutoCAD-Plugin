"""检查排版结果 DXF：
1) 副本的实际旋转/镜像是否等于 XData 里记录的 rot/mirror（旋转因子是否被正确应用）
2) 副本是否落在板料框内
3) 副本之间是否存在真实几何重叠（采样面积法）
"""
import sys, math, collections

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

DXF = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\dongw\Desktop\10.dxf"

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
collect = g1["collect"]
make_tf = g1["make_tf"]
area = g1["area"]
analyse = g1["analyse"]
split_entities = g1["split_entities"]
sections = g1["sections"]
g = g1["g"]


def bbox(pts):
    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    return min(xs), min(ys), max(xs), max(ys)


def pip(pt, poly):
    x, y = pt
    inside = False
    n = len(poly)
    for i in range(n):
        x0, y0 = poly[i]
        x1, y1 = poly[(i + 1) % n]
        if (y0 > y) != (y1 > y):
            xin = x0 + (y - y0) * (x1 - x0) / (y1 - y0)
            if x < xin:
                inside = not inside
    return inside


ents = split_entities(sections.get("ENTITIES", []))
inserts = [e for e in ents if e["type"] == "INSERT"]

orig_by_handle = {}
for e in inserts:
    h = e["g"].get(5, [None])[0]
    if h:
        orig_by_handle[h] = e

copies = []
for e in inserts:
    payload = next((v for v in e["g"].get(1000, []) if v.startswith("PART|")), None)
    if not payload:
        continue
    f = payload.split("|")
    if len(f) < 7:
        continue
    copies.append({
        "ent": e,
        "src": f[1],
        "sheet": int(f[2]),
        "idx": int(f[3]),
        "rot": float(f[4]),
        "mir": int(f[5]),
        "forced": int(f[6]),
        "name": e["g"].get(2, ["?"])[0],
        "ins": (g(e, 10), g(e, 20)),
        "bro": g(e, 50),
        "sx": g(e, 41, default=1.0),
        "sy": g(e, 42, default=1.0),
        "layer": e["g"].get(8, ["?"])[0],
    })

print(f"{DXF}: 共 {len(copies)} 个排版副本 / {len(inserts)} 个 INSERT")

# ---------- 1. 旋转/镜像一致性 ----------
bad_rot = 0
bad_scale = 0
samples = []
for c in copies:
    o = orig_by_handle.get(c["src"])
    if o is None:
        continue
    orot = g(o, 50)
    osx = g(o, 41, default=1.0)
    osy = g(o, 42, default=1.0)
    if c["mir"] == 0:
        expect = (orot + c["rot"]) % 360
        expect_sy = osy
    else:
        expect = (c["rot"] - orot) % 360
        expect_sy = -osy
    d = abs((c["bro"] - expect + 180) % 360 - 180)
    ok_rot = d < 0.01
    ok_scale = (abs(abs(c["sy"]) - abs(expect_sy)) < 1e-6) and ((c["sy"] < 0) == (expect_sy < 0))
    if not ok_rot:
        bad_rot += 1
        if len(samples) < 8:
            samples.append(f"  ✗ handle={c['src']} 块={c['name'][:22]} XData rot={c['rot']} mir={c['mir']} "
                           f"原rot={orot} 实际rot={c['bro']} 应为{expect}")
    if not ok_scale:
        bad_scale += 1

print(f"\n[旋转] 实际旋转不符 = {bad_rot} / {len(copies)}")
print(f"[镜像] 实际比例不符 = {bad_scale} / {len(copies)}")
for s in samples:
    print(s)

# ---------- 2. 是否在板料框内 ----------
frames = []
for e in ents:
    if e["type"] in ("LWPOLYLINE", "POLYLINE") and e["g"].get(8, [""])[0].upper().startswith("DEEPNEST_SHEET"):
        if e["type"] == "LWPOLYLINE":
            xs = [float(v) for v in e["g"].get(10, [])]
            ys = [float(v) for v in e["g"].get(20, [])]
        else:
            xs = [g(v, 10) for v in e.get("_verts", [])]
            ys = [g(v, 20) for v in e.get("_verts", [])]
        if len(xs) >= 3:
            frames.append((min(xs), min(ys), max(xs), max(ys)))
print(f"\n[板料] 找到 {len(frames)} 个 DEEPNEST_SHEET 框")

outside = 0
worst = []
for c in copies:
    if not frames:
        break
    # 用块定义的几何包围盒
    if c["name"] not in blocks:
        continue
    paths, _ = collect(c["name"], make_tf(*c["ins"], c["sx"], c["sy"], c["bro"],
                                         *base_pts.get(c["name"], (0, 0))))
    pts = [p for pp, _ in paths for p in pp]
    if not pts:
        continue
    x0, y0, x1, y1 = bbox(pts)
    c["bb"] = (x0, y0, x1, y1)
    inside = any(fx0 - 1 <= x0 and y0 >= fy0 - 1 and x1 <= fx1 + 1 and y1 <= fy1 + 1
                 for fx0, fy0, fx1, fy1 in frames)
    if not inside:
        outside += 1
        if len(worst) < 6:
            worst.append(f"  ✗ {c['name'][:22]} sheet={c['sheet']} bbox=[{x0:.0f},{y0:.0f} - {x1:.0f},{y1:.0f}]")
print(f"[板料] 不在任何板料框内的副本 = {outside} / {len(copies)}")
for w in worst:
    print(w)

# ---------- 3. 真实重叠 ----------
for c in copies:
    if "bb" not in c:
        c["bb"] = None

items = [c for c in copies if c["bb"]]
items.sort(key=lambda c: c["bb"][0])
overlaps = []
for i in range(len(items)):
    a = items[i]
    for j in range(i + 1, len(items)):
        b = items[j]
        if b["bb"][0] > a["bb"][2]:
            break
        ax0, ay0, ax1, ay1 = a["bb"]
        bx0, by0, bx1, by1 = b["bb"]
        ix0, iy0 = max(ax0, bx0), max(ay0, by0)
        ix1, iy1 = min(ax1, bx1), min(ay1, by1)
        if ix1 <= ix0 or iy1 <= iy0:
            continue
        if a["sheet"] != b["sheet"]:
            continue
        # 采样估计重叠面积
        n = 12
        hits = 0
        if a["name"] in blocks and b["name"] in blocks:
            pa = [p for pp, _ in collect(a["name"], make_tf(*a["ins"], a["sx"], a["sy"], a["bro"],
                                                           *base_pts.get(a["name"], (0, 0)))) for p in pp]
            pb = [p for pp, _ in collect(b["name"], make_tf(*b["ins"], b["sx"], b["sy"], b["bro"],
                                                           *base_pts.get(b["name"], (0, 0)))) for p in pp]
            la = max((l for l in analyse([(pa, True)], 0.1)[0]), key=area, default=[])
            lb = max((l for l in analyse([(pb, True)], 0.1)[0]), key=area, default=[])
            if len(la) >= 3 and len(lb) >= 3:
                for sx in range(n):
                    for sy in range(n):
                        x = ix0 + (ix1 - ix0) * (sx + 0.5) / n
                        y = iy0 + (iy1 - iy0) * (sy + 0.5) / n
                        if pip((x, y), la) and pip((x, y), lb):
                            hits += 1
                frac = hits / (n * n)
                if frac > 0.15:
                    overlaps.append((frac, a, b))

print(f"\n[重叠] 疑似真实重叠对 = {len(overlaps)}")
overlaps.sort(key=lambda t: -t[0])
for frac, a, b in overlaps[:12]:
    print(f"  {frac*100:5.1f}%  {a['name'][:20]}(sheet{a['sheet']},rot{a['rot']:g},mir{a['mir']}) "
          f"× {b['name'][:20]}(sheet{b['sheet']},rot{b['rot']:g},mir{b['mir']})")
