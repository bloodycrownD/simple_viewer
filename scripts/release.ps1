# Release 打包脚本：.NET 自包含 + WinAppSDK 框架依赖发布（免装 .NET 8，解压即用；
# 前置条件：目标机需装一次 Windows App SDK 1.6 运行时，https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads）
# 注意（2026-09-19）：WindowsAppSDKSelfContained=true 的自包含布局存在 XAML 资源解析缺陷
#       （ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml 无法定位，启动即崩——
#       自包含 unpackaged 无包图，框架 pri 子图登记机制未打通），v1.0.0 起改为框架依赖 + 运行时前置说明。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release.ps1 [-Version 1.0.0]
# 版本：未指定时优先取 git describe（最近 tag），无 tag 回退 0.0.0-dev。
# 产物：release\SimpleViewer-v<版本>-win-x64.zip（内含 viewer.exe、.NET 运行时、散装 .xbf 与图标）。
# 注意：与 build.ps1 同坑——先杀运行中的 viewer（锁 DLL）；双 csproj 踩踏走定向 restore。

param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$root = Join-Path $PSScriptRoot ".."
$project = Join-Path $root "SimpleViewer.csproj"

if ([string]::IsNullOrWhiteSpace($Version)) {
    $gitTag = git describe --tags --always 2>$null
    if ($LASTEXITCODE -eq 0 -and $gitTag -match '^v?(\d[\w.\-]*)$') {
        $Version = $Matches[1]
    } else {
        $Version = "0.0.0-dev"
    }
}

Write-Host "[release] 版本：$Version" -ForegroundColor Cyan

# 运行中的 viewer 锁 DLL：发布输出复制会失败（Get-Process 判断避免无进程时的 stderr 中断）
if (Get-Process -Name viewer -ErrorAction SilentlyContinue) {
    Write-Host "[release] 终止运行中的 viewer..." -ForegroundColor Yellow
    Stop-Process -Name viewer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

# 双 csproj 同目录踩踏（RULE）：还原必须定向主工程，防 Core assets 覆盖 UI 工程
Write-Host "[release] 定向还原..." -ForegroundColor Cyan
dotnet msbuild $project -t:Restore -p:Platform=x64 -nologo -v:q
if ($LASTEXITCODE -ne 0) { Write-Host "[release] 还原失败" -ForegroundColor Red; exit 1 }

# 降级还原防御（RULE：间歇不生成 nuget.g.props，assets 丢 RID 目标，发布时报 NETSDK1047）：补一次强制还原
$gprops = Join-Path $root "obj\SimpleViewer.csproj.nuget.g.props"
if (-not (Test-Path $gprops)) {
    Write-Host "[release] 还原产物异常（缺 nuget.g.props），--force 补还原..." -ForegroundColor Yellow
    dotnet restore $project --force --nologo -v:q
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $gprops)) { Write-Host "[release] 还原失败" -ForegroundColor Red; exit 1 }
}

# .NET 自包含发布：WinAppSDK 走框架依赖（机器运行时 + bootstrap 包图解析，见文件头注释）
$outDir = Join-Path $root "release\publish"
$publishArgs = @($project, "-c", "Release", "-r", "win-x64",
    "--self-contained", "true", "-p:Platform=x64",
    "-o", $outDir, "--nologo", "-v:q")
$published = $false
for ($attempt = 1; $attempt -le 2; $attempt++) {
    Write-Host "[release] 发布（Release / win-x64 / 自包含）第 $attempt/2 次尝试..." -ForegroundColor Cyan
    dotnet publish @publishArgs
    if ($LASTEXITCODE -eq 0 -and (Test-Path (Join-Path $outDir "viewer.exe"))) {
        $published = $true
        break
    }
    Write-Host "[release] 第 $attempt 次尝试失败（退出码 $LASTEXITCODE）" -ForegroundColor Yellow
    if ($attempt -lt 2) {
        # XamlCompiler 冷启动竞态预热：直接执行一次失败遗留的 input.json，下一次发布即可通过
        # （与 build.ps1 注释头同一现象：疑似 Defender 实时扫描冷启动竞态，MSB3073 退出码 1 无输出）
        $inputJson = Get-ChildItem (Join-Path $root "obj") -Recurse -Filter input.json -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match 'Release' } | Select-Object -First 1
        $xc = Get-ChildItem (Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windowsappsdk") -Recurse -Filter XamlCompiler.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match 'net472' } | Sort-Object FullName -Descending | Select-Object -First 1
        if ($inputJson -and $xc) {
            Write-Host "[release] 预热 XamlCompiler（$($inputJson.Directory.Name)）..." -ForegroundColor Yellow
            & $xc.FullName $inputJson.FullName (Join-Path $inputJson.Directory "output.json") | Out-Null
        }
        Start-Sleep -Seconds 2
    }
}
if (-not $published) { Write-Host "[release] 发布失败" -ForegroundColor Red; exit 1 }

# 打 zip
$zipPath = Join-Path $root "release\SimpleViewer-v$Version-win-x64.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Write-Host "[release] 打包 zip..." -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $outDir "*") -DestinationPath $zipPath
$sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)

Write-Host "[release] 完成：$zipPath（$sizeMb MB）" -ForegroundColor Green
