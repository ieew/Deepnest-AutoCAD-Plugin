# DeepNest for AutoCAD（.NET 移植版）

把 [Deepnest](https://deepnest.io) 的排样内核移植为 .NET，作为 **AutoCAD 插件** 直接对图纸里的块参照做排版。
面向激光切割：选一块板料 + 一堆零件块，自动排样，结果以「克隆块参照」的形式写回图纸。

- **不炸开块、不改块名**：结果全部是原块的副本（同一 BlockTableRecord、同一图层），`SELECTSIMILAR` 仍可用；
- **零件一个都不丢**：板料装不下的件会独占一张新板强制放好并标记；
- **孔可以嵌套**：零件内部闭合轮廓视为孔，小件可以排进大件的孔里；
- **断线自动接合**：外轮廓拐角接缝（实测 2~20 个单位）自动按最小容差焊上，避免「把角上的小缺口当成零件」；
- **零件间隙生效**（`OuterNfp` 外扩 间距/2，孔内缩 间距/2）；
- 结果列表按 **板材利用率** 从高到低排序，最多保留 500 个不同方案供挑选。

## 环境要求

| 项 | 要求 |
|---|---|
| AutoCAD | 2026（R25.1，.NET 8）；`Adobe` 之外的旧版本需改引用 |
| .NET SDK | 8.0（x64） |
| 平台 | Windows x64 |

AutoCAD 程序集从 `C:\Program Files\Autodesk\AutoCAD 2026\` 引用（`acdbmgd` / `accoremgd` / `acmgd` / `AdWindows`，`Private=false`）。
如安装路径不同，用 `-p:AcadDir=...` 覆盖。

## 目录结构

```
src/DeepNest.Core/     纯算法内核（几何 / NFP / 遗传算法 / 摆放），不依赖 AutoCAD
src/DeepNest.Cad/      AutoCAD 插件（块几何提取 / 结果写入 / 预览 / WPF 面板）
tests/DeepNest.Core.Tests/  xUnit 测试
tools/Probe/           内核诊断控制台（不在解决方案内）
tools/*.py             只读 DXF 分析脚本（排版结果校验、块轮廓质量、矩阵约定验证等）

addon.cc, main/, main.js, renderer.js, package.json, ...   ← 原始 Electron 版原型（保留作参考）
```

## 编译

> **首次编译前**：确认 `src/DeepNest.Cad/DeepNest.Cad.csproj` 里的 `<AcadDir>` 指向你本机 AutoCAD 安装目录
> （默认 `C:\Program Files\Autodesk\AutoCAD 2026`）。**路径末尾不要带反斜杠**——MSBuild 命令行里
> `-p:AcadDir=...\2026\` 的尾反斜杠会转义引号，导致 AutoCAD 引用全部失效（报一堆 `CS0246`）。

```powershell
$dn = "C:\Users\dongw\AppData\Local\Microsoft\dotnet\dotnet.exe"

# 常规
& $dn build DeepNestCad.sln -c Release

# AutoCAD 正在运行、DLL 被占用时（跳过项目引用复制，先单独编译 Core）
& $dn build src/DeepNest.Core/DeepNest.Core.csproj -c Release
& $dn build src/DeepNest.Cad/DeepNest.Cad.csproj -c Release -p:BuildProjectReferences=false

# AutoCAD 装在别处
& $dn build src/DeepNest.Cad/DeepNest.Cad.csproj -c Release "-p:AcadDir=D:\Autodesk\AutoCAD 2026"
```

### 一键出包（推荐）

```powershell
.\build-release.ps1                                     # Release 编译 + 单测 + bundle + zip
.\build-release.ps1 -AcadDir "D:\Autodesk\AutoCAD 2026" # 指定 AutoCAD 目录
.\build-release.ps1 -SkipTests -NoZip                   # 只编译并组装 bundle
```

输出：

- `src/DeepNest.Cad/bin/Release/DeepNest.Cad.dll`（插件本体）
- `src/DeepNest.Cad/bin/Release/DeepNest.Core.dll`（内核，需与插件同目录）
- `dist/DeepNest.bundle/`（AutoCAD 标准分发包，`PackageContents.xml` 里的版本号自动取自程序集）
- `dist/DeepNest-v<版本>.zip`（分发包压缩）

> 脚本是 UTF-8 **带 BOM** 保存的——PowerShell 5.1 读取不带 BOM 的 `.ps1` 会按 ANSI 解码，中文会乱码并报语法错。编辑后请保留 BOM。


## 安装 / 使用

**方式一：NETLOAD**

```
NETLOAD  →  src\DeepNest.Cad\bin\Release\DeepNest.Cad.dll
```

**方式二：`.bundle`（推荐分发）**

把 `dist\DeepNest.bundle` 整个目录复制到
`%APPDATA%\Autodesk\ApplicationPlugins\`（当前用户）或
`%PROGRAMDATA%\Autodesk\ApplicationPlugins\`（所有用户），AutoCAD 启动时自动加载。

**命令**

| 命令 | 说明 |
|---|---|
| `DEEPNEST` | 打排样面板（主要入口） |
| `DEEPNESTPICK` | 面板「选择对象」按钮在命令上下文里执行选择 |
| `DEEPNESTCLEAN` | 删除上一次的排版结果（只删本插件写的对象，不动原始块） |
| `DEEPNESTINFO` | 版本 / 设置文件 / 图纸单位 |
| `DEEPNESTDUMP` | 把识别出的板料/零件轮廓导出 JSON（诊断用） |

**流程**：`DEEPNEST` → 选择对象（1 条闭合多段线作板料 + 若干零件块）→ 设置参数 → 开始 → 在结果列表里点选方案看预览 → 应用。

**关键参数**：零件间隙、端点焊接容差、曲线离散精度、旋转档数、种群/变异率、最长运行秒、最大世代、镜像、每行板数与行列间距。

## 坐标与写入约定（改代码前务必看）

- 排版内核的坐标是「平移空间」：`t` 直接加在零件的归一化坐标上。
  最终落位 `world = R(θ)·S·q + T`，其中 `q = 世界几何 − po`，`T = t + 板料偏移 + 排版原点`。
- **镜像约定 `S = diag(-1,1)`，即 x → −x**（见 `GeometryUtil.Mirror`）。
- **AutoCAD `Matrix3d` 是标准数学（列向量）约定：`A * B` = 先应用 B、再应用 A**。
  所以"先镜像→再旋转→最后平移"必须写成 `Displacement(T) * Rotation(θ) * mirror`，最右端是最先应用的。
- 写结果不使用 `TransformBy`（矩阵分解会把镜像揉成负比例/额外 180°），而是显式设置
  `Rotation / ScaleFactors / Position`（`ResultWriter.ApplyPlacement`）。
- 预览矩阵 `ResultWriter.BuildNormalizedPlacementMatrix` 是**唯一真源**：
  预览、写入校核（`ExpectedBBoxMin`）、板料网格（`SheetOrigin`）都由它推导。

## 测试

```powershell
& $dn test tests/DeepNest.Core.Tests/DeepNest.Core.Tests.csproj
```

覆盖几何基础、NFP（含"零件不在原点"与旋转的回归用例）、遗传算法、摆放（孔内嵌套 / 不缺件 / 间隙）。

## 诊断

- `%TEMP%\deepnest-apply.log`：每次应用写前 20 件的 `t / rot / mir / po / ins / expected / actual / d`，
  以及 `PREVIEW ... intersect=` 行。面板「最大位置偏差」应为 `0.000`。
- `tools/dxf_check.py`：校验 DXF 结果里副本的旋转/镜像是否符合 XData 记录、是否落在板料框内、是否有真实重叠。
- `tools/dxf_loops.py` / `dxf_used.py` / `dxf_verify.py`：块轮廓质量与容差修复效果分析。

## 原始 Electron 原型

仓库顶层仍保留原 Deepnest（Electron + node-gyp）源码，仅作算法参考，不再维护。
新功能开发都在 `src/` 下。
