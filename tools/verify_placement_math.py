"""三路对照：写入(ApplyPlacement) / 预览(BuildNormalizedPlacementMatrix) / 目标(R·S·(w-po)+T)。

- 写入：块参照显式设置 Rotation/ScaleFactors/Position 后被 AutoCAD 应用的变换
        A = Rot(θ∓φ)·diag(∓sx, sy)，Position' = R(θ)·S·(ins−po) + T
- 预览：归一化轮廓 q = w − po 直接乘矩阵 mirror*Rot(θ)*Disp(T)
- 目标：world' = R(θ)·S·(w − po) + T
镜像约定 S = diag(-1, 1)：x → -x（与 GeometryUtil.Mirror 一致）。
"""
import math
import random


def rot(a):
    c, s = math.cos(math.radians(a)), math.sin(math.radians(a))
    return ((c, -s), (s, c))


def mul(A, B):
    return tuple(tuple(sum(A[i][k] * B[k][j] for k in range(2)) for j in range(2)) for i in range(2))


def apply(A, p):
    return (A[0][0] * p[0] + A[0][1] * p[1], A[1][0] * p[0] + A[1][1] * p[1])


def add(p, q):
    return (p[0] + q[0], p[1] + q[1])


def sub(p, q):
    return (p[0] - q[0], p[1] - q[1])


S_MIRROR = ((-1, 0), (0, 1))   # 引擎镜像：x → -x
IDENT = ((1, 0), (0, 1))

defs = [(0, 0), (10, 20), (-5, 7), (3, -8)]
base = (2, 3)
random.seed(2026)

bad_write = 0
bad_preview = 0
total = 0

for _ in range(80):
    phi = random.choice([0, 0, 30, 90, 45, 180, 270])          # 原块参照自带旋转
    sx, sy = random.choice([(1, 1), (2, 1), (1.5, 1.5), (-1, 1)])
    ins = (random.uniform(-50, 50), random.uniform(-50, 50))
    theta = random.choice([0, 90, 180, 270])                   # 排版旋转
    mir = random.choice([0, 1])
    T = (random.uniform(-100, 100), random.uniform(-100, 100))

    A0 = mul(rot(phi), ((sx, 0), (0, sy)))

    def world(p):
        return add(apply(A0, sub(p, base)), ins)

    ws = [world(p) for p in defs]
    po = (min(p[0] for p in ws), min(p[1] for p in ws))        # 归一化时减掉的包围盒左下角

    # 目标
    def target(p):
        q = sub(world(p), po)
        if mir:
            q = apply(S_MIRROR, q)
        return add(apply(rot(theta), q), T)

    # 写入路径（ApplyPlacement）
    new_phi = theta - phi if mir else theta + phi
    new_sx = -sx if mir else sx
    A_write = mul(rot(new_phi), ((new_sx, 0), (0, sy)))
    rel = sub(ins, po)
    if mir:
        rel = apply(S_MIRROR, rel)
    pos = add(apply(rot(theta), rel), T)

    # 预览路径（BuildNormalizedPlacementMatrix：linear = mirror*rot，平移 = T）
    # 列向量约定下 M=S·Rot 表示"先旋转再镜像"，而我们要"先镜像再旋转"，所以写 Rot·S。
    # （C# 里 Matrix3d 是行向量约定，mirror*Rotation 恰好就是"先镜像后旋转"，两者等价。）
    A_preview = mul(rot(theta), S_MIRROR if mir else IDENT)

    for p in defs:
        got_write = add(apply(A_write, sub(p, base)), pos)      # 块几何经写入变换
        got_preview = add(apply(A_preview, sub(world(p), po)), T)  # 归一化点乘预览矩阵
        exp = target(p)

        total += 1
        if math.dist(got_write, exp) > 1e-9:
            bad_write += 1
            if bad_write <= 3:
                print(f"✗ 写入不一致 phi={phi} s=({sx},{sy}) θ={theta} mir={mir} p={p} got={got_write} exp={exp}")
        if math.dist(got_preview, exp) > 1e-9:
            bad_preview += 1
            if bad_preview <= 3:
                print(f"✗ 预览不一致 phi={phi} s=({sx},{sy}) θ={theta} mir={mir} p={p} got={got_preview} exp={exp}")

print(f"对照 {total} 个点：写入不一致 {bad_write}，预览不一致 {bad_preview}")
