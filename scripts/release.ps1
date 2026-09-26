# Release 打包脚本：.NET 自包含 + WinAppSDK 框架依赖发布（免装 .NET 8，解压即用；
# 前置条件：目标机需装一次 Windows App SDK 2.5 运行时，https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads）
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

# 双 csproj 同目录踩踏（RULE）：两工程共享 obj\project.assets.json，msbuild 对主工程的还原会
# **并行**传递还原 Core——两个还原竞写同一文件、后写完者胜（"间歇踩踏"的真根因，2026-09-19 定论）。
# 确定性修法：显式串行——Core 先还原（此后其传递再还原为 no-op 不落盘），主工程压轴还原。
# -nr:false 防 MSBuild 节点复用携带陈旧"已还原"状态跳过落盘。校验双条件见 Test-AssetsOk。
Write-Host "[release] 定向还原（Core → 主工程 串行）..." -ForegroundColor Cyan
$gprops = Join-Path $root "obj\SimpleViewer.csproj.nuget.g.props"
$assetsPath = Join-Path $root "obj\project.assets.json"
function Test-AssetsOk {
    (Test-Path $gprops) -and (Test-Path $assetsPath) -and
        (Select-String -Path $assetsPath -Pattern '/win-x64' -SimpleMatch -Quiet) -and
        (Select-String -Path $assetsPath -Pattern 'Microsoft.WindowsAppSDK' -SimpleMatch -Quiet)
}
$coreProject = Join-Path $root "SimpleViewer.Core.csproj"
dotnet msbuild $coreProject -t:Restore -p:Platform=x64 -nr:false -nologo -v:q
# RestoreRecursive=false：主工程还原完全不碰 Core 节点（单次 msbuild 内部两者并行竞写
# 同一 assets、后写完者胜——让主工程作为唯一写者压轴，竞态根除，2026-09-19 定论）。
dotnet msbuild $project -t:Restore -p:Platform=x64 -p:RestoreRecursive=false -nr:false -nologo -v:q
if ($LASTEXITCODE -ne 0 -or -not (Test-AssetsOk)) {
    Write-Host "[release] 还原产物异常（缺 RID 目标或主工程包签名），清缓存强制重还原..." -ForegroundColor Yellow
    Remove-Item $assetsPath, $gprops -Force -ErrorAction SilentlyContinue
    dotnet msbuild $coreProject -t:Restore -p:Platform=x64 -p:RestoreForce=true -nr:false -nologo -v:q
    dotnet msbuild $project -t:Restore -p:Platform=x64 -p:RestoreForce=true -nr:false -nologo -v:q
    if ($LASTEXITCODE -ne 0 -or -not (Test-AssetsOk)) { Write-Host "[release] 还原失败" -ForegroundColor Red; exit 1 }
}

# .NET 自包含发布：WinAppSDK 走框架依赖（机器运行时 + bootstrap 包图解析，见文件头注释）
# 输出目录必须先清空：增量 publish 会保留上一代 .xbf 搭配新 viewer.dll，
# 运行时 XBF/程序集代际错配 → "Failed to assign ItemsRepeater.ItemTemplate" 启动崩（2026-09-19 实锤）。
$outDir = Join-Path $root "release\publish"
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
$publishArgs = @($project, "-c", "Release", "-r", "win-x64",
    "--self-contained", "true", "-p:Platform=x64",
    "-o", $outDir, "--no-restore", "--nologo", "-v:q")
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
        # XamlCompiler 位置随 2.x 包拆分而变（1.6 在元包 microsoft.windowsappsdk\tools\net472，
        # 2.x 在子包 microsoft.windowsappsdk.winui\tools\net472）——按两族 glob、版本目录取最新；
        # 只扫元包会拿到旧编译器（预热错版本等于没预热）。
        $xc = Get-ChildItem (Join-Path $env:USERPROFILE ".nuget\packages") -Directory -Filter "microsoft.windowsappsdk*" -ErrorAction SilentlyContinue |
            ForEach-Object { Get-ChildItem $_.FullName -Directory -ErrorAction SilentlyContinue } |
            ForEach-Object {
                $exe = Join-Path $_.FullName "tools\net472\XamlCompiler.exe"
                if (Test-Path $exe) {
                    [PSCustomObject]@{ Exe = $exe; Ver = $(try { [version]$_.Name } catch { [version]"0.0.0" }) }
                }
            } | Sort-Object Ver -Descending | Select-Object -First 1
        if ($inputJson -and $xc) {
            Write-Host "[release] 预热 XamlCompiler（$($inputJson.Directory.Name) / $($xc.Ver)）..." -ForegroundColor Yellow
            & $xc.Exe $inputJson.FullName (Join-Path $inputJson.Directory "output.json") | Out-Null
        }
        Start-Sleep -Seconds 2
    }
}
if (-not $published) { Write-Host "[release] 发布失败" -ForegroundColor Red; exit 1 }

# 散装资源补拷：dotnet publish 不拷贝 unpackaged 布局的 .xbf 与图标（ms-appx:/// 按文件解析，
# 缺失即启动崩 Cannot locate MainWindow.xaml / ItemTemplate 赋值失败——2026-09-19 实锤，
# 依赖目录累积残留曾掩盖此问题并引发 XBF/程序集代际错配）。
$binOut = Join-Path $root "bin\x64\Release\net8.0-windows10.0.19041.0\win-x64"
Copy-Item (Join-Path $binOut "*.xbf") $outDir -Force
Copy-Item (Join-Path $binOut "Views") $outDir -Recurse -Force
Copy-Item (Join-Path $binOut "Assets") (Join-Path $outDir "Assets") -Recurse -Force
if (-not (Test-Path (Join-Path $outDir "MainWindow.xbf"))) { Write-Host "[release] 补拷后仍缺 MainWindow.xbf" -ForegroundColor Red; exit 1 }

# 打 zip
$zipPath = Join-Path $root "release\SimpleViewer-v$Version-win-x64.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Write-Host "[release] 打包 zip..." -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $outDir "*") -DestinationPath $zipPath
$sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)

Write-Host "[release] 完成：$zipPath（$sizeMb MB）" -ForegroundColor Green
