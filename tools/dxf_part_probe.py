"""针对单个零件块做深入诊断：实体重叠/开口端/各级容差下的成环情况，并判断哪个环才真正包住所有几何。"""
import sys, math, collections

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

DXF = sys.argv[1] if len(sys.argv) > 1 else "0.DXF"
TARGET = sys.argv[2] if len(sys.argv) > 2 else None

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


names = [TARGET] if TARGET else [
    n for n in blocks
    if not n.startswith("*") and any(n == e["g"].get(2, [""])[0]
                                     for e in split_entities(sections.get("ENTITIES", []))
                                     if e["type"] == "INSERT")]

for name in names:
    if name not in blocks:
        print(f"!! 找不到块 {name}")
        continue

    paths, stats = collect(name, make_tf(0, 0, 1, 1, 0, 0, 0))
    allpts = [p for pp, _ in paths for p in pp]
    if not allpts:
        print(f"!! 块 {name} 无几何")
        continue

    gx0, gy0, gx1, gy1 = bbox(allpts)
    gw, gh = gx1 - gx0, gy1 - gy0
    diag = math.hypot(gw, gh)
    print(f"\n==== 块 [{name}] 实体={dict(stats)}")
    print(f"几何 bbox = ({gx0:.2f},{gy0:.2f}) - ({gx1:.2f},{gy1:.2f})  尺寸 {gw:.2f} x {gh:.2f}  对角 {diag:.2f}")

    print(f"\n路径明细（{len(paths)} 条）:")
    for i, (pp, closed) in enumerate(paths):
        x0, y0, x1, y1 = bbox(pp)
        chord = math.dist(pp[0], pp[-1])
        print(f"  #{i:<2} closed={int(closed)} n={len(pp):<4} 起=({pp[0][0]:.2f},{pp[0][1]:.2f}) "
              f"末=({pp[-1][0]:.2f},{pp[-1][1]:.2f}) 首尾距={chord:.3f} "
              f"bbox=[{x0:.1f},{y0:.1f} - {x1:.1f},{y1:.1f}]")

    # 各级容差
    print("\n各级焊接容差下的结果:")
    ladder = [0.1, 0.3, 1, 3, 10, diag * 0.005, diag * 0.01, diag * 0.03, diag * 0.05, diag * 0.1]
    for tol in ladder:
        loops, open_ends = analyse(paths, tol)
        loops = [l for l in loops if len(l) >= 3 and area(l) > 1e-6]
        if not loops:
            print(f"  tol={tol:<8.2f} 环=0 开口端={len(open_ends)}")
            continue
        top = max(loops, key=area)
        rb = bbox(top)
        # 判断最大环是否包住所有几何点
        outside = sum(1 for p in allpts if not pip(p, top))
        print(f"  tol={tol:<8.2f} 环={len(loops):<3} 开口端={len(open_ends):<3} "
              f"最大环 area={area(top):>12.1f} bbox={rb[2]-rb[0]:.1f}x{rb[3]-rb[1]:.1f} "
              f"外面点数={outside}/{len(allpts)}")

    # 开口端配对距离（tol=0.1 时）
    loops, open_ends = analyse(paths, 0.1)
    if len(open_ends) >= 2:
        print(f"\ntol=0.1 开口端 {len(open_ends)} 个，最近配对距离:")
        used = set()
        pairs = []
        for i in range(len(open_ends)):
            for j in range(i + 1, len(open_ends)):
                pairs.append((math.dist(open_ends[i], open_ends[j]), i, j))
        pairs.sort()
        for d, i, j in pairs[:10]:
            print(f"  {d:8.3f}  ({open_ends[i][0]:.2f},{open_ends[i][1]:.2f}) <-> ({open_ends[j][0]:.2f},{open_ends[j][1]:.2f})")
