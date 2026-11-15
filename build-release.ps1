#requires -Version 5.1
<#
.SYNOPSIS
    一键生成 DeepNest for AutoCAD 稳定版（Release 编译 + ./bundle 打包 + zip）。

.EXAMPLE
    .\build-release.ps1
    .\build-release.ps1 -AcadDir "D:\Autodesk\AutoCAD 2026\" -NoZip
    .\build-release.ps1 -SkipTests

.NOTES
    - AutoCAD 正在运行也可以：只写 Release 目录，不碰被加载的 Debug 产物。
    - 首次在新机器上编译时用 -AcadDir 指向本机 AutoCAD 安装目录。
#>
[CmdletBinding()]
param(
    [string]$AcadDir = "C:\Program Files\Autodesk\AutoCAD 2026",
    [string]$DotnetPath = "",
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $root

# 去掉结尾斜杠：MSBuild 命令行里 "...2026\" 的尾反斜杠会转义引号，导致引用全部失效。
$AcadDir = $AcadDir.TrimEnd('\', '/')

function Write-Step([string]$text) {
    Write-Host ""
    Write-Host "== $text" -ForegroundColor Cyan
}

function Resolve-Dotnet {
    param([string]$Explicit)

    $candidates = @()
    if ($Explicit) { $candidates += $Explicit }
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe") }
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles "dotnet\dotnet.exe") }

    foreach ($candidate in $candidates) {
        if (-not (Test-Path $candidate)) { continue }
        $sdks = & $candidate --list-sdks 2>$null
        if ($sdks -and ($sdks -match '^8\.')) {
            return $candidate
        }
    }

    throw "找不到带 .NET 8 SDK 的 dotnet。请安装 .NET 8 SDK，或用 -DotnetPath 指定（当前候选：$($candidates -join '; ')）。"
}

$dotnet = Resolve-Dotnet -Explicit $DotnetPath
Write-Host "dotnet : $dotnet"
Write-Host "AcadDir: $AcadDir"

if (-not (Test-Path (Join-Path $AcadDir "acdbmgd.dll"))) {
    Write-Warning "在 '$AcadDir' 下没找到 acdbmgd.dll，若编译报缺引用请用 -AcadDir 指定正确的 AutoCAD 安装目录。"
}

# ---------- 1. 编译 ----------
Write-Step "编译 DeepNest.Core ($Configuration)"
& $dotnet build "src/DeepNest.Core/DeepNest.Core.csproj" -c $Configuration --nologo -v q "-p:AcadDir=$AcadDir"
if ($LASTEXITCODE -ne 0) { throw "DeepNest.Core 编译失败。" }

Write-Step "编译 DeepNest.Cad ($Configuration)"
# BuildProjectReferences=false：避免 AutoCAD 占用 DeepNest.Core.dll 时复制失败（Core 已在上面编好）
& $dotnet build "src/DeepNest.Cad/DeepNest.Cad.csproj" -c $Configuration --nologo -v q `
    -p:BuildProjectReferences=false "-p:AcadDir=$AcadDir"
if ($LASTEXITCODE -ne 0) { throw "DeepNest.Cad 编译失败。" }

# ---------- 2. 自检 ----------
if (-not $SkipTests) {
    Write-Step "运行单元测试"
    & $dotnet test "tests/DeepNest.Core.Tests/DeepNest.Core.Tests.csproj" -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "单元测试失败，已中止发布。" }
}

# ---------- 3. 打包 bundle ----------
$outDir = "src/DeepNest.Cad/bin/$Configuration"
$bundle = Join-Path $root "dist/DeepNest.bundle"
$content = Join-Path $bundle "Contents/Windows"

Write-Step "组装 dist/DeepNest.bundle"
if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force }
New-Item -ItemType Directory -Force -Path $content | Out-Null

$files = @("DeepNest.Cad.dll", "DeepNest.Core.dll", "Clipper2Lib.dll", "DeepNest.Cad.pdb")
foreach ($file in $files) {
    $source = Join-Path $outDir $file
    if (-not (Test-Path $source)) { throw "缺少产物：$source" }
    Copy-Item $source $content -Force
}
if (Test-Path "icon.ico") { Copy-Item "icon.ico" $content -Force }

$assembly = [System.Reflection.AssemblyName]::GetAssemblyName((Resolve-Path (Join-Path $content "DeepNest.Cad.dll")))
$version = $assembly.Version.ToString(3)

$template = Get-Content "packaging/PackageContents.xml" -Raw -Encoding UTF8
$packageContents = $template -replace "__APPVERSION__", $version
$target = Join-Path $bundle "PackageContents.xml"
[System.IO.File]::WriteAllText($target, $packageContents, (New-Object System.Text.UTF8Encoding($false)))

# 校验生成的 XML
try {
    [xml]$check = [System.IO.File]::ReadAllText($target, [System.Text.Encoding]::UTF8)
    if (-not $check.ApplicationPackage) { throw "结构不对" }
}
catch {
    throw "生成的 PackageContents.xml 无法解析：$($_.Exception.Message)"
}

# ---------- 4. 可选 zip ----------
$zipPath = $null
if (-not $NoZip) {
    Write-Step "压缩分发包"
    $zipPath = Join-Path $root "dist/DeepNest-v$version.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path $bundle -DestinationPath $zipPath -CompressionLevel Optimal
}

# ---------- 5. 汇总 ----------
Write-Step "完成"
Get-ChildItem -Recurse $content -File | ForEach-Object {
    "  {0,-24} {1,10:N0} 字节" -f $_.Name, $_.Length
}
Write-Host ""
Write-Host "版本      : $version" -ForegroundColor Green
Write-Host "bundle    : $bundle"
if ($zipPath) { Write-Host "zip       : $zipPath" }
Write-Host ""
Write-Host "安装方式（任选其一）："
Write-Host "  1) 把 DeepNest.bundle 复制到 %APPDATA%\Autodesk\ApplicationPlugins\，重启 AutoCAD"
Write-Host "  2) 或 NETLOAD 加载 $content\DeepNest.Cad.dll"
