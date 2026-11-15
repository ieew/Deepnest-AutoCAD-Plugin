"""列出模型空间实际引用的块（按数量），并对这些块做轮廓质量分析。"""
import sys, math, collections

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

DXF = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\dongw\Desktop\1.dxf"

src = open(r"c:\Users\dongw\Desktop\新建文件夹\Deepnest\tools\dxf_loops.py", encoding="utf-8").read()
head = src.split('print(f"tol={TOL}')[0]
g1 = {"__name__": "loopsmod"}
_backup = sys.argv
sys.argv = [sys.argv[0], DXF, "0.1"]
try:
    exec(compile(head, "dxf_loops_head", "exec"), g1)
finally:
    sys.argv = _backup

blocks = g1["blocks"]
collect = g1["collect"]
analyse = g1["analyse"]
make_tf = g1["make_tf"]
area = g1["area"]
split_entities = g1["split_entities"]
sections = g1["sections"]
g = g1["g"]

# 模型空间实体里的 INSERT 统计
ents = split_entities(sections.get("ENTITIES", []))
counts = collections.Counter()
for e in ents:
    if e["type"] == "INSERT":
        name = e["g"].get(2, ["?"])[0]
        counts[name] += 1

print("模型空间引用块统计（按数量）:")
for name, cnt in counts.most_common(30):
    print(f"  {cnt:>4}  {name}")
print(f"共 {len(counts)} 种块，{sum(counts.values())} 个 INSERT")

print("\n各块轮廓分析（tol=0.1，只看被引用的块）:")
print(f"{'块名':<40}{'引用':>4}{'路径':>5}{'闭环':>4}{'开口端':>6}{'尺寸':>18}{'最大环':>12}{'环/bbox':>9}")
for name, cnt in counts.most_common():
    if name not in blocks:
        print(f"{name:<40}{cnt:>4}   !块定义缺失")
        continue
    paths, stats = collect(name, make_tf(0, 0, 1, 1, 0, 0, 0))
    if not paths:
        print(f"{name:<40}{cnt:>4}{0:>5}{0:>4}{0:>6}   !无几何  {dict(stats)}")
        continue
    loops, open_ends = analyse(paths, 0.1)
    allpts = [p for pp, _ in paths for p in pp]
    minx = min(p[0] for p in allpts); maxx = max(p[0] for p in allpts)
    miny = min(p[1] for p in allpts); maxy = max(p[1] for p in allpts)
    ba = (maxx - minx) * (maxy - miny)
    la = max((area(l) for l in loops if len(l) >= 3), default=0.0)
    print(f"{name:<40}{cnt:>4}{len(paths):>5}{len(loops):>4}{len(open_ends):>6}"
          f"{maxx-minx:>9.0f}x{maxy-miny:<8.0f}{la:>12.0f}{(la/ba if ba>0 else 0):>9.2f}")
