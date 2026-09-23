# repro-freeze.ps1 - 复现扫描后 UI 冻结：120 张 5000x3500 大 JPEG + 启动监测 45s 看门狗
Add-Type -AssemblyName System.Drawing
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$libDir = Join-Path $env:TEMP 'sv-freeze-lib'
if (Test-Path $libDir) { Remove-Item $libDir -Recurse -Force }
New-Item -ItemType Directory -Path $libDir | Out-Null

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$bmp = New-Object System.Drawing.Bitmap 5000, 3500
$g = [System.Drawing.Graphics]::FromImage($bmp)
$rand = New-Object System.Random(42)
for ($i = 1; $i -le 120; $i++) {
  # 每张不同渐变底色（解码成本真实，文件尺寸可控）
  $c1 = [System.Drawing.Color]::FromArgb(255, ($rand.Next(256)), ($rand.Next(256)), ($rand.Next(256)))
  $c2 = [System.Drawing.Color]::FromArgb(255, ($rand.Next(256)), ($rand.Next(256)), ($rand.Next(256)))
  $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Rectangle(0,0,5000,3500)), $c1, $c2, 45)
  $g.FillRectangle($brush, 0, 0, 5000, 3500)
  $g.DrawString(('IMG ' + $i), (New-Object System.Drawing.Font('Arial', 120, [System.Drawing.FontStyle]::Bold)), [System.Drawing.Brushes]::White, 200, 1500)
  $bmp.Save((Join-Path $libDir ('big_{0:d3}.jpg' -f $i)), [System.Drawing.Imaging.ImageFormat]::Jpeg)
  $brush.Dispose()
}
$g.Dispose(); $bmp.Dispose()
Write-Output ('GEN-DONE ' + $sw.ElapsedMilliseconds + 'ms, files: ' + (Get-ChildItem $libDir).Count)

# qa/C-1 以脚本自身目录（scripts\）锚定仓库根推导 exe 路径，仓库克隆到任意路径/机器可用
$exe = Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = $settings + '.bak-frz'
# qa/B-1 备份前置守卫：残留备份 = 上次运行中途崩溃（finally 未执行）——先还原再重新备份，
# 防止把已被污染的 settings 当作新备份源、唯一好备份被删/覆盖
if (Test-Path $backup) {
  Move-Item $backup $settings -Force
  Write-Output ('STALE-BACKUP-RESTORED: ' + $backup + ' 已还原为 settings，随后重新备份')
}
Copy-Item $settings $backup -Force
try {
  $json = Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json
  $json.LastLibraryRoot = $libDir
  $json.shortcuts = @()
  $json.tagGroups = @()
  $json | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding UTF8

  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 800
  $markBefore = (Get-Item "$env:LOCALAPPDATA\SimpleViewer\logs\startup.log").Length
  Start-Process $exe
  Start-Sleep -Seconds 45
  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 600
  $fs = [IO.File]::OpenRead("$env:LOCALAPPDATA\SimpleViewer\logs\startup.log")
  $fs.Seek($markBefore, 'Begin') | Out-Null
  $sr = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)
  $newLog = $sr.ReadToEnd(); $sr.Dispose()
  Write-Output '=== 本次运行新增日志（关键行） ==='
  foreach ($line in ($newLog -split "`r?`n")) {
    if ($line -match 'UI 无响应|UI 鏃犲搷搴|scan:|thumb:decode|Unhandled') {
      Write-Output ('  ' + $line.Trim())
    }
  }
  $decodeCount = ([regex]::Matches($newLog, 'thumb:decode')).Count
  Write-Output ('DECODE-COUNT: ' + $decodeCount)
}
finally {
  Move-Item $backup $settings -Force
  Write-Output 'SETTINGS-RESTORED'
}
