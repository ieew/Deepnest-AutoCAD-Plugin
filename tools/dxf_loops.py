"""只读分析 DXF 中每个块定义的轮廓质量：
- 离散各实体（LINE/ARC/CIRCLE/LWPOLYLINE/POLYLINE/ELLIPSE/SPLINE近似）
- 展开块内 INSERT
- 端点焊接（容差可调）→ 找闭环 → 报告最大环面积 vs 几何包围盒面积
用于定位 "外轮廓断了 → 把角上的小缺口当成零件" 的问题。
"""
import sys, math, collections

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

path = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\dongw\Desktop\1.dxf"
TOL = float(sys.argv[2]) if len(sys.argv) > 2 else 0.1

with open(path, "r", encoding="utf-8", errors="ignore") as f:
    raw = [ln.rstrip("\r\n") for ln in f]

pairs = []
i = 0
while i + 1 < len(raw):
    c = raw[i].strip()
    v = raw[i + 1]
    i += 2
    try:
        c = int(c)
    except ValueError:
        continue
    pairs.append((c, v.strip()))


def split_entities(records):
    out, ent = [], None
    for code, val in records:
        if code == 0:
            if ent is not None:
                out.append(ent)
            ent = {"type": val, "g": collections.defaultdict(list), "raw": []}
        elif ent is not None:
            ent["g"][code].append(val)
            ent["raw"].append((code, val))
    if ent is not None:
        out.append(ent)
    return out


def lwpoly_vertices(ent):
    """按 DXF 原始顺序解析 LWPOLYLINE 顶点 -> [[x, y, bulge], ...]。

    bulge(42) 紧跟在所属顶点之后，且 bulge=0 时会被省略，
    所以不能用「按序号平铺」的方式配对。
    """
    verts = []
    cur = None
    for code, val in ent.get("raw", []):
        if code == 10:
            cur = [float(val), 0.0, 0.0]
            verts.append(cur)
        elif code == 20 and cur is not None:
            cur[1] = float(val)
        elif code == 42 and cur is not None:
            cur[2] = float(val)
    return verts


def g(ent, code, idx=0, default=0.0):
    vals = ent["g"].get(code)
    if not vals:
        return default
    try:
        return float(vals[idx])
    except (ValueError, IndexError):
        return default


# ---------- 分节 ----------
sections, cur, buf = {}, None, []
for code, val in pairs:
    if code == 0 and val == "SECTION":
        buf, cur = [], None
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

# ---------- 块定义 ----------
blocks = {}          # name -> entities
base_pts = {}
cur_name, cur_ents, pending_poly = None, None, None

for ent in split_entities(sections.get("BLOCKS", [])):
    t = ent["type"]
    if t == "BLOCK":
        cur_name = ent["g"].get(2, ["?"])[0]
        cur_ents = []
        blocks[cur_name] = cur_ents
        base_pts[cur_name] = (g(ent, 10), g(ent, 20))
        pending_poly = None
        continue
    if t == "ENDBLK":
        cur_name, cur_ents = None, None
        pending_poly = None
        continue
    if cur_ents is None:
        continue
    if t == "VERTEX" and pending_poly is not None:
        pending_poly.append(ent)
        continue
    if t == "SEQEND":
        pending_poly = None
        continue
    cur_ents.append(ent)
    if t == "POLYLINE":
        pending_poly = []

# 把 POLYLINE 与其 VERTEX 合并成一条记录
for name, ents in blocks.items():
    merged = []
    for e in ents:
        if e["type"] != "VERTEX":
            merged.append(e)
    for e in merged:
        e["_verts"] = []
    cur = None
    for e in ents:
        if e["type"] == "POLYLINE":
            cur = e
        elif e["type"] == "VERTEX" and cur is not None:
            cur["_verts"].append(e)
        elif e["type"] != "VERTEX":
            cur = None
    blocks[name] = merged


# ---------- 离散 ----------
def tess_line(e, tf):
    return [tf(g(e, 10), g(e, 20)), tf(g(e, 11), g(e, 21))], False


def tess_arc(e, tf):
    cx, cy, r = g(e, 10), g(e, 20), g(e, 40)
    a0, a1 = g(e, 50), g(e, 51)
    if r <= 0:
        return [], False
    if a1 <= a0:
        a1 += 360.0
    sweep = a1 - a0
    n = max(6, int(abs(sweep) / 6) + 2)
    pts = []
    for k in range(n + 1):
        a = math.radians(a0 + sweep * k / n)
        pts.append(tf(cx + r * math.cos(a), cy + r * math.sin(a)))
    return pts, False


def tess_circle(e, tf):
    cx, cy, r = g(e, 10), g(e, 20), g(e, 40)
    if r <= 0:
        return [], False
    n = 64
    return [tf(cx + r * math.cos(2 * math.pi * k / n), cy + r * math.sin(2 * math.pi * k / n))
            for k in range(n)], True


def tess_ellipse(e, tf):
    cx, cy = g(e, 10), g(e, 20)
    mx, my = g(e, 11), g(e, 21)
    ratio = g(e, 40, default=1.0)
    t0, t1 = g(e, 41), g(e, 42, default=2 * math.pi)
    if t1 <= t0:
        t1 += 2 * math.pi
    closed = abs((t1 - t0) - 2 * math.pi) < 1e-6
    n = 64
    pts = []
    for k in range(n + 1):
        t = t0 + (t1 - t0) * k / n
        pts.append(tf(cx + mx * math.cos(t) - my * ratio * math.sin(t),
                      cy + my * math.cos(t) + mx * ratio * math.sin(t)))
    return pts, closed


def tess_lwpoly(e, tf):
    verts = lwpoly_vertices(e)
    closed = bool(int(g(e, 70)) & 1)
    pts = [(v[0], v[1], v[2]) for v in verts]
    if closed and pts:
        pts.append(pts[0])
    out = []
    for k in range(len(pts) - 1):
        x0, y0, b = pts[k]
        x1, y1, _ = pts[k + 1]
        out.append(tf(x0, y0))
        if abs(b) > 1e-9:
            # 凸度圆弧
            d = math.hypot(x1 - x0, y1 - y0)
            if d > 1e-12:
                theta = 4 * math.atan(b)
                r = d / (2 * math.sin(abs(theta) / 2))
                # 圆心
                mx, my = (x0 + x1) / 2, (y0 + y1) / 2
                h = math.sqrt(max(r * r - (d / 2) ** 2, 0))
                ux, uy = -(y1 - y0) / d, (x1 - x0) / d
                sign = 1 if b > 0 else -1
                cx = mx + ux * h * sign * (1 if theta > 0 else 1)
                cy = my + uy * h * sign
                a0 = math.atan2(y0 - cy, x0 - cx)
                a1 = math.atan2(y1 - cy, x1 - cx)
                if b > 0 and a1 <= a0:
                    a1 += 2 * math.pi
                if b < 0 and a1 >= a0:
                    a1 -= 2 * math.pi
                steps = max(4, int(abs(a1 - a0) / math.radians(6)) + 1)
                for s in range(1, steps):
                    a = a0 + (a1 - a0) * s / steps
                    out.append(tf(cx + r * math.cos(a), cy + r * math.sin(a)))
    if pts:
        out.append(tf(pts[-1][0], pts[-1][1]))
    return out, closed


def tess_poly(e, tf):
    verts = e.get("_verts", [])
    closed = bool(int(g(e, 70)) & 1)
    pts = [tf(g(v, 10), g(v, 20)) for v in verts]
    if closed and pts:
        pts.append(pts[0])
    return pts, closed


def tess_spline(e, tf):
    xs = [float(v) for v in e["g"].get(11, [])] or [float(v) for v in e["g"].get(10, [])]
    ys = [float(v) for v in e["g"].get(21, [])] or [float(v) for v in e["g"].get(20, [])]
    n = min(len(xs), len(ys))
    if n < 2:
        return [], False
    return [tf(xs[k], ys[k]) for k in range(n)], False


def make_tf(dx, dy, sx, sy, rot, bx, by):
    c, s = math.cos(math.radians(rot)), math.sin(math.radians(rot))

    def tf(x, y):
        x -= bx
        y -= by
        x, y = x * sx, y * sy
        return (bx + x * c - y * s + dx, by + x * s + y * c + dy)

    return tf


def collect(name, tf, depth=0, out=None, stats=None):
    if out is None:
        out = []
        stats = collections.Counter()
    ents = blocks.get(name)
    if ents is None or depth > 5:
        stats["missing_block"] += 1
        return out, stats
    for e in ents:
        t = e["type"]
        stats[t] += 1
        if t == "LINE":
            p, c = tess_line(e, tf)
        elif t == "ARC":
            p, c = tess_arc(e, tf)
        elif t == "CIRCLE":
            p, c = tess_circle(e, tf)
        elif t == "ELLIPSE":
            p, c = tess_ellipse(e, tf)
        elif t == "LWPOLYLINE":
            p, c = tess_lwpoly(e, tf)
        elif t == "POLYLINE":
            p, c = tess_poly(e, tf)
        elif t == "SPLINE":
            p, c = tess_spline(e, tf)
        elif t == "INSERT":
            sub = e["g"].get(2, [None])[0]
            sx, sy = g(e, 41, default=1.0), g(e, 42, default=1.0)
            rot = g(e, 50)
            bx, by = base_pts.get(sub, (0, 0))
            sub_tf0 = make_tf(g(e, 10), g(e, 20), sx, sy, rot, bx, by)
            collect(sub, lambda x, y, f=sub_tf0, o=tf: o(*f(x, y)), depth + 1, out, stats)
            continue
        else:
            p, c = [], False
        if len(p) >= 2:
            out.append((p, c))
    return out, stats


def analyse(paths, tol):
    """端点焊接 + 贪心成环，返回 (loops, open_end_points)"""
    cell = max(tol, 1e-9)
    nodes = []
    grid = collections.defaultdict(list)

    def node(p):
        cx, cy = int(math.floor(p[0] / cell)), int(math.floor(p[1] / cell))
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for idx in grid[(cx + dx, cy + dy)]:
                    q = nodes[idx]
                    if (q[0] - p[0]) ** 2 + (q[1] - p[1]) ** 2 <= tol * tol:
                        return idx
        idx = len(nodes)
        nodes.append(p)
        grid[(cx, cy)].append(idx)
        return idx

    closed_loops, chains = [], []
    for pts, closed in paths:
        if closed:
            closed_loops.append(pts)
        else:
            chains.append(pts)

    edges = []
    for ch in chains:
        a, b = node(ch[0]), node(ch[-1])
        if a == b:
            closed_loops.append(ch)
        else:
            edges.append((ch, a, b))

    adj = collections.defaultdict(list)
    for i, (ch, a, b) in enumerate(edges):
        adj[a].append(i)
        adj[b].append(i)

    used = [False] * len(edges)
    loops = list(closed_loops)
    open_end_nodes = set()
    for start in range(len(edges)):
        if used[start]:
            continue
        used[start] = True
        ch, a, b = edges[start]
        walk = list(ch)
        cur, came = b, start
        while True:
            nxt = [i for i in adj[cur] if not used[i]]
            if not nxt:
                open_end_nodes.add(cur)
                break
            i = nxt[0]
            used[i] = True
            ch2, a2, b2 = edges[i]
            pts = ch2 if a2 == cur else list(reversed(ch2))
            walk.extend(pts[1:])
            cur = b2 if a2 == cur else a2
            if cur == a:
                loops.append(walk)
                break
    for i, (ch, a, b) in enumerate(edges):
        if not used[i]:
            open_end_nodes.add(a)
            open_end_nodes.add(b)
    return loops, [nodes[i] for i in open_end_nodes]


def area(pts):
    s = 0.0
    for k in range(len(pts)):
        x0, y0 = pts[k]
        x1, y1 = pts[(k + 1) % len(pts)]
        s += x0 * y1 - x1 * y0
    return abs(s) / 2


print(f"tol={TOL}  blocks={len(blocks)}")
rows = []
for name in blocks:
    if name.startswith("*"):
        continue
    paths, stats = collect(name, make_tf(0, 0, 1, 1, 0, 0, 0))
    if not paths:
        continue
    loops, open_ends = analyse(paths, TOL)
    allpts = [p for pp, _ in paths for p in pp]
    if not allpts:
        continue
    minx = min(p[0] for p in allpts); maxx = max(p[0] for p in allpts)
    miny = min(p[1] for p in allpts); maxy = max(p[1] for p in allpts)
    bbox_area = (maxx - minx) * (maxy - miny)
    la = sorted((area(l) for l in loops if len(l) >= 3), reverse=True)
    best = la[0] if la else 0.0
    rows.append((best / bbox_area if bbox_area > 0 else 0, name, len(paths), len(loops),
                 len(open_ends), bbox_area, best, la[:4], dict(stats)))

rows.sort()
print(f"{'块名':<24}{'路径':>5}{'环':>4}{'开口端':>7}{'bbox面积':>13}{'最大环面积':>13}{'覆盖比':>9}")
bad = 0
for cover, name, np_, nl, no, ba, best, tops, stats in rows:
    flag = ""
    if cover < 0.85:
        flag = "  <== 可疑"
        bad += 1
    print(f"{name:<24}{np_:>5}{nl:>4}{no:>7}{ba:>13.1f}{best:>13.1f}{cover:>9.3f}{flag}")
print(f"\n可疑块数(最大环覆盖 <85% bbox) = {bad} / {len(rows)}")
