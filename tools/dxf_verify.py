"""模拟 C# ContourBuilder 的新逻辑（容差梯子 + 可信度校验），验证真实块的结果。"""
import sys, math, collections

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

DXF = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\dongw\Desktop\1.dxf"

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
collect = g1["collect"]
analyse = g1["analyse"]
make_tf = g1["make_tf"]
area = g1["area"]
split_entities = g1["split_entities"]
sections = g1["sections"]


def bbox(pts):
    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    return min(xs), min(ys), max(xs), max(ys)


def ladder(base, diag):
    cands = [base, base * 3, 1, 3, 10]
    if diag > 1e-9:
        cands += [diag * 0.005, diag * 0.01, diag * 0.03]
    cap = diag * 0.05 if diag > 1e-9 else base * 100
    cap = max(cap, base)
    out = []
    for v in sorted(cands):
        if v < base - 1e-12 or v > cap:
            continue
        if out and abs(out[-1] - v) < 1e-9:
            continue
        out.append(v)
    return out or [base]


def cluster_filter(paths, base=0.1):
    """与 C# ClusterPaths/ChooseCluster 一致：按包围盒邻近度单连接聚类，取路径数最多的一簇。"""
    bs = [bbox(pp) for pp, _ in paths]
    maxpd = max((math.hypot(b[2] - b[0], b[3] - b[1]) for b in bs), default=0.0)
    th = max(base * 10, maxpd * 0.03)
    n = len(paths)
    parent = list(range(n))

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    for i in range(n):
        for j in range(i + 1, n):
            a, b = bs[i], bs[j]
            dx = max(0.0, max(a[0] - b[2], b[0] - a[2]))
            dy = max(0.0, max(a[1] - b[3], b[1] - a[3]))
            if math.hypot(dx, dy) <= th:
                ra, rb = find(i), find(j)
                if ra != rb:
                    parent[rb] = ra

    groups = collections.defaultdict(list)
    for i in range(n):
        groups[find(i)].append(i)

    def score(idxs):
        xs = [bs[i][0] for i in idxs] + [bs[i][2] for i in idxs]
        ys = [bs[i][1] for i in idxs] + [bs[i][3] for i in idxs]
        return (len(idxs), (max(xs) - min(xs)) * (max(ys) - min(ys)))

    best = max(groups.values(), key=score)
    kept = [paths[i] for i in best]
    return kept, n - len(best)


def simulate(name, base=0.1):
    paths, stats = collect(name, make_tf(0, 0, 1, 1, 0, 0, 0))
    if not paths:
        return None
    paths, dropped = cluster_filter(paths, base)
    allpts = [p for pp, _ in paths for p in pp]
    minx, miny, maxx, maxy = bbox(allpts)
    gw, gh = maxx - minx, maxy - miny
    diag = math.hypot(gw, gh)

    best = None
    used_tol = None
    for tol in ladder(base, diag):
        loops, open_ends = analyse(paths, tol)
        loops = [l for l in loops if len(l) >= 3 and area(l) > max(0.1 ** 2, 1e-9)]
        maxa = max((area(l) for l in loops), default=0.0)
        cand = (len(open_ends), maxa, tol, loops)
        if best is None or (cand[0], -cand[1]) < (best[0], -best[1]):
            best = cand
        used_tol = tol
        if not open_ends and loops:
            break
    if best is None or not best[3]:
        return ("无轮廓", None, used_tol, gw, gh, 0)
    open_count, maxa, tol, loops = best
    top = max(loops, key=area)
    rx0, ry0, rx1, ry1 = bbox(top)
    rw, rh = rx1 - rx0, ry1 - ry0
    trustworthy = True
    if gw > 1e-9 and rw < gw * 0.5:
        trustworthy = False
    if gh > 1e-9 and rh < gh * 0.5:
        trustworthy = False
    kind = "OK" if trustworthy else "→外包矩形"
    if trustworthy and (open_count > 0 or tol > base * 1.5):
        kind = "OK(黄色/已接合)"
    return (kind, (rw, rh, area(top)), tol, gw, gh, open_count)


ents = split_entities(sections.get("ENTITIES", []))
counts = collections.Counter(e["g"].get(2, ["?"])[0] for e in ents if e["type"] == "INSERT")

print(f"{'块名':<40}{'引用':>4}{'容差':>8}{'几何尺寸':>16}{'外轮廓尺寸':>16}{'开口':>5}  结果")
for name, cnt in counts.most_common():
    if name not in blocks:
        continue
    r = simulate(name)
    if r is None:
        continue
    kind, lw, tol, gw, gh, oc = r
    if lw is None:
        print(f"{name:<40}{cnt:>4}{tol:>8.2f}{gw:>8.0f}x{gh:<7.0f}{'-':>16}{oc:>5}  {kind}")
        continue
    rw, rh, ra = lw
    print(f"{name:<40}{cnt:>4}{tol:>8.2f}{gw:>8.0f}x{gh:<7.0f}{rw:>8.0f}x{rh:<7.0f}{oc:>5}  {kind}")
