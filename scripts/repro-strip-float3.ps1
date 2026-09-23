# repro-strip-float3.ps1 - 超宽横图 -d -i 直开单图：验证横条下方空白（「返回悬空」形态）
# 注入路径：viewer -d <dir> -i 1（走查 5 验证过的可靠直开路径，绕开 Enter/双击注入坑）
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W11 {
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[W11]::SetProcessDPIAware() | Out-Null

$exe = Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = $settings + '.bak-sf3'
if (Test-Path $backup) {
  Move-Item $backup $settings -Force
  Write-Output ('STALE-BACKUP-RESTORED: ' + $backup)
}
Copy-Item $settings $backup -Force
$libDir = Join-Path $env:TEMP 'sv-verify-lib-sf3'
$shotDir = Join-Path $env:TEMP 'sv-verify-sf3'
if (Test-Path $shotDir) { Remove-Item $shotDir -Recurse -Force }
New-Item -ItemType Directory -Path $shotDir | Out-Null

try {
  if (Test-Path $libDir) { Remove-Item $libDir -Recurse -Force }
  New-Item -ItemType Directory -Path $libDir | Out-Null
  # 超宽横图 4:1（w1，直开目标）+ 竖图 2:3（w2 对照）
  $bmp = New-Object System.Drawing.Bitmap 1200, 300
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::FromArgb(180, 40, 40))
  $g.Dispose(); $bmp.Save((Join-Path $libDir 'w1[风景].jpg'), [System.Drawing.Imaging.ImageFormat]::Jpeg); $bmp.Dispose()
  $bmp = New-Object System.Drawing.Bitmap 400, 600
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::FromArgb(40, 120, 60))
  $g.Dispose(); $bmp.Save((Join-Path $libDir 'w2.jpg'), [System.Drawing.Imaging.ImageFormat]::Jpeg); $bmp.Dispose()

  $json = Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json
  $json.LastLibraryRoot = $libDir
  $json.shortcuts = @()
  $json.tagGroups = @(
    [PSCustomObject]@{ id = 'vg1'; name = '主题'; exclusive = $false; tags = @([PSCustomObject]@{ id = 'vt1'; name = '风景' }) }
  )
  $json | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding UTF8

  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 900
  Start-Process $exe -ArgumentList @('-d', $libDir, '-i', '1')
  Start-Sleep -Seconds 9
  $proc = Get-Process viewer -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  [W11]::MoveWindow($proc.MainWindowHandle, 40, 40, 1500, 950, $true) | Out-Null
  [W11]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
  Start-Sleep -Milliseconds 1500

  # PrintWindow 截窗口本体（不依赖前台——屏幕 CopyFromScreen 会被用户前台窗口遮挡）
  Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W12 {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
}
"@
  $w = 1500; $h = 950
  $bmp2 = New-Object System.Drawing.Bitmap $w, $h
  $g2 = [System.Drawing.Graphics]::FromImage($bmp2)
  $hdc = $g2.GetHdc()
  [W12]::PrintWindow($proc.MainWindowHandle, $hdc, 2) | Out-Null
  $g2.ReleaseHdc($hdc)
  $bmp2.Save((Join-Path $shotDir 'direct-wide.png'), [System.Drawing.Imaging.ImageFormat]::Png)

  # 像素剖面：画布中心列（x=790），逐段识别 颜色带（chrome 39,39,46 / 窗口底 ~32 / 红图 180,40,40）
  $cx = 790
  $prev = ''; $segStart = -1
  for ($y = 90; $y -lt 990; $y++) {
    $c = $bmp2.GetPixel($cx, $y)
    $kind = if ($c.R -gt 130 -and $c.G -lt 90) { 'RED' }
            elseif ($c.R -ge 34 -and $c.R -le 44 -and $c.G -ge 34 -and $c.G -le 44) { 'CHROME' }
            elseif ($c.R -ge 25 -and $c.R -lt 34) { 'DARKBG' }
            else { 'OTHER' }
    if ($kind -ne $prev) {
      if ($segStart -ge 0 -and $prev -ne 'OTHER') { Write-Output ($prev + ': y' + $segStart + '..' + ($y - 1) + '（' + ($y - $segStart) + 'px）') }
      $segStart = $y; $prev = $kind
    }
  }
  Write-Output ($prev + ': y' + $segStart + '..989')
  $bmp2.Dispose()
  Write-Output 'DONE（窗口保留在屏幕上供用户查看）'
  # 不 kill：窗口留给用户实机确认
}
finally {
  Start-Sleep -Milliseconds 400
  Move-Item $backup $settings -Force
  Write-Output 'SETTINGS-RESTORED（viewer 窗口保留）'
}
