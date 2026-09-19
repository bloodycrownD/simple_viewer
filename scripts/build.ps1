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

$solution = Join-Path $PSScriptRoot "..\SimpleViewer.sln"

# 前置强制还原：obj 缺失时普通还原有增量误判跳过 UI 项目的已知问题
Write-Host "[build] 前置还原（--force）..." -ForegroundColor Cyan
dotnet restore $solution --force --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "[build] 还原失败" -ForegroundColor Red
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

# 全部原样重试失败：最后做一次完整冷重建（清 obj + 强制还原）
Write-Host "[build] 原样重试均失败，执行完整冷重建..." -ForegroundColor Yellow
Remove-Item -Recurse -Force (Join-Path $PSScriptRoot "..\obj") -ErrorAction SilentlyContinue
dotnet restore $solution --force --nologo -v q | Out-Null
dotnet build $solution -c $Configuration -p:Platform=x64 --nologo -v q -m:1 -nr:false
if ($LASTEXITCODE -eq 0) {
    Write-Host "[build] 冷重建成功" -ForegroundColor Green
    exit 0
}

Write-Host "[build] 仍失败。若错误为 XamlCompiler MSB3073 沉默崩溃：可手动执行一次该 exe（预热）后重跑本脚本；或考虑为 NuGet 包缓存目录添加 Defender 排除项（需用户决策）。参考 spec 环境风险条目。" -ForegroundColor Red
exit 1
