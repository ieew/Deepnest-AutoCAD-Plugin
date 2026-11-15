"""用真实日志反证 Matrix3d 的组合顺序。

若代码写成 `mirror * Rot * Disp`（= 先平移再旋转，错误），则
    expected = 实际正确值的"先平移再旋转"版本 = min(L·(q+T)) = actual + (L − I)·T
其中 L = R(θ)·S 是线性部分。所以对每组日志数据检验：
    expected ?= actual + (L − I)·T
成立就证明顺序确实反了（并且修复后 expected 应直接等于 actual）。
"""
import re
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

LOG = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\dongw\AppData\Local\Temp\deepnest-apply.log"

num = r"(-?\d+(?:\.\d+)?)"
head_re = re.compile(
    r"^part#(\d+) sheet=(\d+) t=\(" + num + "," + num + r"\) rot=" + num +
    r" mir=(\d) po=\(" + num + "," + num + r"\) ins=\(" + num + "," + num +
    r"\) oro=" + num + r" oscale=\(" + num + "," + num + r"\) so=\(" + num + "," + num +
    r"\) origin=\(" + num + "," + num + r"\)")
tail_re = re.compile(
    r"^part#(\d+) expected=\(" + num + "," + num + r"\) actual=\(" + num + "," + num + r"\)")


def rot(theta):
    import math
    c, s = round(math.cos(math.radians(theta)), 12), round(math.sin(math.radians(theta)), 12)
    return ((c, -s), (s, c))


def matmul(A, B):
    return tuple(tuple(sum(A[i][k] * B[k][j] for k in range(2)) for j in range(2)) for i in range(2))


def apply(A, p):
    return (A[0][0] * p[0] + A[0][1] * p[1], A[1][0] * p[0] + A[1][1] * p[1])


S = ((-1.0, 0.0), (0.0, 1.0))
IDENT = ((1.0, 0.0), (0.0, 1.0))

pending = {}
checked = 0
ok = 0
bad = []

for line in open(LOG, encoding="utf-8", errors="replace"):
    line = line.strip()
    m = head_re.match(line)
    if m:
        g = m.groups()
        pending[int(g[0])] = {
            "t": (float(g[2]), float(g[3])),
            "rot": float(g[4]),
            "mir": int(g[5]),
            "so": (float(g[13]), float(g[14])),
            "origin": (float(g[15]), float(g[16])),
        }
        continue

    m = tail_re.match(line)
    if not m:
        continue

    idx = int(m.group(1))
    if idx not in pending:
        continue
    d = pending[idx]
    expected = (float(m.group(2)), float(m.group(3)))
    actual = (float(m.group(4)), float(m.group(5)))

    T = (d["t"][0] + d["so"][0] + d["origin"][0],
         d["t"][1] + d["so"][1] + d["origin"][1])

    # 旧代码 `mirror * Rot * Disp` 的线性部分 = S·R；正确实现是 R·S。
    # 两者只在 mir=1 且 θ∈{90,270} 时不同，所以这两种情况单独归类。
    L = matmul(S if d["mir"] else IDENT, rot(d["rot"]))

    delta = apply(L, T)
    delta = (delta[0] - T[0], delta[1] - T[1])
    pred = (actual[0] + delta[0], actual[1] + delta[1])

    err = max(abs(pred[0] - expected[0]), abs(pred[1] - expected[1]))
    checked += 1
    if err <= 0.01:
        ok += 1
    else:
        bad.append((idx, d["rot"], d["mir"], expected, pred, err))

print(f"检查 {checked} 组日志数据：恒等式 expected = actual + (L−I)·T 成立 {ok}，不成立 {len(bad)}")
print("（L 用旧代码的 S·R。恒等式成立 = expected 确实是「把平移一起旋转了」算出来的）")
for b in bad[:5]:
    print(f"  part#{b[0]} rot={b[1]:g} mir={b[2]} expected={b[3]} 预测={b[4]} 误差={b[5]:.6f}")
if bad:
    print("剩余差异均为 mir=1 且 rot∈{90,270}：此时 S·R ≠ R·S，"
          "旧写法等价于「先平移→再旋转→再镜像」，与公式近似但多一层镜像次序差异。")
