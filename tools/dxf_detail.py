"""深入分析若干块的实体结构、环面积、开口端间距。"""
import sys, math, collections
sys.path.insert(0, r"c:\Users\dongw\Desktop\新建文件夹\Deepnest\tools")
import importlib.util

spec = importlib.util.spec_from_file_location(
    "loops", r"c:\Users\dongw\Desktop\新建文件夹\Deepnest\tools\dxf_loops.py")

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

DXF = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\dongw\Desktop\1.dxf"
TARGETS = sys.argv[2:]

# 复用 dxf_loops 的解析（不执行其主流程：手工内联执行前 60%）
src = open(r"c:\Users\dongw\Desktop\新建文件夹\Deepnest\tools\dxf_loops.py", encoding="utf-8").read()
head = src.split('print(f"tol={TOL}')[0]


class _FakeSys:
    argv = [sys.argv[0], DXF, "0.1"]
    stdout = sys.stdout


g1 = {"__name__": "loopsmod"}
_argv_backup = sys.argv
sys.argv = [sys.argv[0], DXF, "0.1"]
try:
    exec(compile(head, "dxf_loops_head", "exec"), g1)
finally:
    sys.argv = _argv_backup

blocks = g1["blocks"]
base_pts = g1["base_pts"]
collect = g1["collect"]
analyse = g1["analyse"]
make_tf = g1["make_tf"]
area = g1["area"]

names = TARGETS or list(blocks.keys())
for name in names:
    if name not in blocks:
        # 允许用 "真实块名" 直接匹配
        cand = [k for k in blocks if k == name]
        if not cand:
            print(f"\n### {name}: 未找到")
            continue
        name = cand[0]
    paths, stats = collect(name, make_tf(0, 0, 1, 1, 0, 0, 0))
    loops, open_ends = analyse(paths, 0.1)
    allpts = [p for pp, _ in paths for p in pp]
    minx = min(p[0] for p in allpts); maxx = max(p[0] for p in allpts)
    miny = min(p[1] for p in allpts); maxy = max(p[1] for p in allpts)
    print(f"\n### {name}  实体={dict(stats)}")
    print(f"  路径={len(paths)} 闭环={len(loops)} 开口端={len(open_ends)} "
          f"bbox=[{minx:.2f},{miny:.2f} - {maxx:.2f},{maxy:.2f}] "
          f"尺寸={maxx-minx:.1f}x{maxy-miny:.1f}")
    tops = sorted((area(l) for l in loops), reverse=True)[:6]
    print(f"  环面积 top: {[round(a,1) for a in tops]}")
    # 环的包围盒
    for l in sorted(loops, key=area, reverse=True)[:3]:
        xs = [p[0] for p in l]; ys = [p[1] for p in l]
        print(f"    环 area={area(l):.1f} n={len(l)} bbox=[{min(xs):.1f},{min(ys):.1f} - {max(xs):.1f},{max(ys):.1f}]")

    # 开口端两两最近距离：说明缺口有多大
    if len(open_ends) >= 2:
        ds = []
        for i in range(len(open_ends)):
            best = 1e18
            for j in range(len(open_ends)):
                if i == j:
                    continue
                d = math.dist(open_ends[i], open_ends[j])
                best = min(best, d)
            ds.append(best)
        ds.sort()
        print(f"  开口端最近配对距离(前8): {[round(d,3) for d in ds[:8]]}")
        print(f"  开口端坐标: {[(round(p[0],1), round(p[1],1)) for p in open_ends[:10]]}")

    # 各路径长度分布（找离群的长线/远点）
    lens = sorted((math.dist(pp[0], pp[-1]) for pp, _ in paths), reverse=True)
    print(f"  路径弦长 top: {[round(v,1) for v in lens[:6]]}")

    # 逐路径的包围盒（找飞得很远的实体）
    far = []
    for pp, closed in paths:
        xs = [p[0] for p in pp]; ys = [p[1] for p in pp]
        far.append((min(xs), min(ys), max(xs), max(ys), len(pp), closed))
    cx = (minx + maxx) / 2; cy = (miny + maxy) / 2
    diag = math.hypot(maxx - minx, maxy - miny)
    for f in far:
        fx = (f[0] + f[2]) / 2; fy = (f[1] + f[3]) / 2
        if diag > 0 and math.dist((fx, fy), (cx, cy)) > diag * 0.45:
            print(f"    ⚠ 离群路径 center=({fx:.1f},{fy:.1f}) n={f[4]} closed={f[5]} "
                  f"bbox=[{f[0]:.1f},{f[1]:.1f} - {f[2]:.1f},{f[3]:.1f}]")
