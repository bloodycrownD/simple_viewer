# 构建脚本：单节点禁复用 + 分级失败重试
# 背景：WinAppSDK 1.6.240923002 的 XamlCompiler 存在间歇沉默崩溃（退出码 1 无输出，MSB3073）。
#       实测规律（2026-09-16）：
#         - 默认并行参数失败率最高（5/6）；-m:1 -nr:false 显著降低；
#         - 失败集中在"删 obj + restore 后的冷构建"；同一 input.json 手动执行 XamlCompiler 成功后，
#           紧接的 MSBuild 构建即可通过（疑似 Defender 实时扫描冷启动竞态）。
#       因此重试策略：前置强制还原，失败先原样重试（利用预热后的缓存），冷重建只作最后手段。
param(
    [string]$Configuration = "Debug",
    [int]$MaxAttempts = 3
)

$root = Join-Path $PSScriptRoot ".."
$solution = Join-Path $root "SimpleViewer.sln"
$mainProject = Join-Path $root "SimpleViewer.csproj"
$coreProject = Join-Path $root "SimpleViewer.Core.csproj"
$testsProject = Join-Path $root "tests\SimpleViewer.Tests\SimpleViewer.Tests.csproj"

# 前置还原：**串行定向**（tests → Core → 主工程 RestoreRecursive=false），与 release.ps1 同法。
# 禁用 `dotnet restore <sln> --force`：双 csproj 共享 obj\project.assets.json，主工程与其传递
# Core 的还原在同一 msbuild 内并行竞写、后写完者胜——主工程视角丢 WinAppSDK 引用，表现是
# XamlCompiler 报 WMC1007 "Cannot resolve metadata for WinUI types"（2026-09-26 实锤，
# 与 RULE「双 csproj 同目录同 TFM 的还原踩踏」同源）。
function Invoke-TargetedRestore([switch]$Force) {
    $f = if ($Force) { "-p:RestoreForce=true" } else { $null }
    dotnet restore $testsProject --nologo -v q | Out-Null
    dotnet msbuild $coreProject -t:Restore -p:Platform=x64 -nr:false -nologo -v:q @f | Out-Null
    # RestoreRecursive=false：主工程还原完全不碰 Core 节点，作为唯一写者压轴
    dotnet msbuild $mainProject -t:Restore -p:Platform=x64 -p:RestoreRecursive=false -nr:false -nologo -v:q @f | Out-Null
    return (Select-String -Path (Join-Path $root "obj\project.assets.json") -Pattern 'Microsoft.WindowsAppSDK' -SimpleMatch -Quiet)
}

Write-Host "[build] 前置还原（tests → Core → 主工程 串行定向）..." -ForegroundColor Cyan
$assetsOk = Invoke-TargetedRestore
if (-not $assetsOk) {
    Write-Host "[build] 还原产物非主工程视角（并行踩踏残留），清缓存强制重还原..." -ForegroundColor Yellow
    Remove-Item (Join-Path $root "obj\project.assets.json"), (Join-Path $root "obj\SimpleViewer.csproj.nuget.g.props") -Force -ErrorAction SilentlyContinue
    $assetsOk = Invoke-TargetedRestore -Force
}
if ($LASTEXITCODE -ne 0 -or -not $assetsOk) {
    Write-Host "[build] 还原失败（主工程 assets 缺 WinAppSDK 引用）" -ForegroundColor Red
    exit 1
}

for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
    Write-Host "[build] 第 $attempt/$MaxAttempts 次尝试 (-m:1 -nr:false)..." -ForegroundColor Cyan
    dotnet build $solution -c $Configuration -p:Platform=x64 --nologo -v q -m:1 -nr:false | Out-Null
    $code = $LASTEXITCODE
    if ($code -eq 0) {
        Write-Host "[build] 成功（第 $attempt 次尝试）" -ForegroundColor Green
        exit 0
    }
    Write-Host "[build] 失败（退出码 $code）" -ForegroundColor Yellow
    if ($attempt -lt $MaxAttempts) {
        # 原样重试，不做清理——清理 obj 会制造更冷的构建路径，失败率更高
        Start-Sleep -Seconds 2
    }
}

# 全部原样重试失败：最后做一次完整冷重建（清 obj + 串行定向强制还原，同上——勿用 sln 还原）
Write-Host "[build] 原样重试均失败，执行完整冷重建..." -ForegroundColor Yellow
Remove-Item -Recurse -Force (Join-Path $root "obj") -ErrorAction SilentlyContinue
if (-not (Invoke-TargetedRestore -Force)) {
    Write-Host "[build] 冷重建前还原异常（assets 非主工程视角）" -ForegroundColor Red
}
dotnet build $solution -c $Configuration -p:Platform=x64 --nologo -v q -m:1 -nr:false
if ($LASTEXITCODE -eq 0) {
    Write-Host "[build] 冷重建成功" -ForegroundColor Green
    exit 0
}

Write-Host "[build] 仍失败。若错误为 XamlCompiler MSB3073 沉默崩溃：可手动执行一次该 exe（预热）后重跑本脚本；或考虑为 NuGet 包缓存目录添加 Defender 排除项（需用户决策）。参考 spec 环境风险条目。" -ForegroundColor Red
exit 1
