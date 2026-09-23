# repro-strip-float2.ps1 - 超宽横图复现「返回悬空」：图片 Uniform 垂直居中后顶部空白，横条浮空
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W10 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
"@
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[W10]::SetProcessDPIAware() | Out-Null

$exe = Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = $settings + '.bak-sf2'
if (Test-Path $backup) {
  Move-Item $backup $settings -Force
  Write-Output ('STALE-BACKUP-RESTORED: ' + $backup)
}
Copy-Item $settings $backup -Force
$libDir = Join-Path $env:TEMP 'sv-verify-lib-sf2'
$shotDir = Join-Path $env:TEMP 'sv-verify-sf2'
if (Test-Path $shotDir) { Remove-Item $shotDir -Recurse -Force }
New-Item -ItemType Directory -Path $shotDir | Out-Null

function FindBtnByName($win, $name) {
  $bt = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  $nm = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
  $and = New-Object System.Windows.Automation.AndCondition($bt, $nm)
  return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $and)
}
function InvokeEl($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function DblClick($el) {
  $r = $el.Current.BoundingRectangle
  $cx = [int](($r.Left + $r.Right) / 2); $cy = [int](($r.Top + $r.Bottom) / 2)
  [W10]::SetCursorPos($cx, $cy) | Out-Null
  Start-Sleep -Milliseconds 80
  [W10]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [W10]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 60
  [W10]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [W10]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 150
}

try {
  if (Test-Path $libDir) { Remove-Item $libDir -Recurse -Force }
  New-Item -ItemType Directory -Path $libDir | Out-Null
  # 超宽横图（4:1）：fit 后高度仅画布 1/4，垂直居中 → 顶部大空白
  $bmp = New-Object System.Drawing.Bitmap 1200, 300
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::FromArgb(180, 40, 40))
  $g.Dispose(); $bmp.Save((Join-Path $libDir 'w1[风景].jpg'), [System.Drawing.Imaging.ImageFormat]::Jpeg); $bmp.Dispose()
  $bmp = New-Object System.Drawing.Bitmap 200, 150
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::FromArgb(70, 100, 130))
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
  Start-Process $exe
  Start-Sleep -Seconds 9
  $proc = Get-Process viewer -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  [W10]::MoveWindow($proc.MainWindowHandle, 40, 40, 1500, 950, $true) | Out-Null
  [W10]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
  Start-Sleep -Milliseconds 800
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Simple Viewer')
  $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
  if (-not $win) { Write-Output 'NO-WINDOW'; exit 1 }

  # 激活筛选（命中 w1）→ 双击进单图
  $fj = FindBtnByName $win '风景'
  if ($fj) { InvokeEl $fj; Start-Sleep -Milliseconds 1200; Write-Output '筛选已激活' }
  $card = FindBtnByName $win 'w1.jpg'
  if (-not $card) { Write-Output 'CARD-MISSING'; exit 1 }
  $r0 = $card.Current.BoundingRectangle
  Write-Output ('card rect: L' + [int]$r0.Left + ' T' + [int]$r0.Top + ' R' + [int]$r0.Right + ' B' + [int]$r0.Bottom)
  # raw 双击在本机不稳定（WinUI 手势注入坑），改「单击选中 + raw Enter」进单图（图库 Enter 为既有路径）
  [W10]::SetCursorPos([int](($r0.Left + $r0.Right) / 2), [int](($r0.Top + $r0.Bottom) / 2)) | Out-Null
  Start-Sleep -Milliseconds 80
  [W10]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [W10]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 400
  [W10]::keybd_event(0x0D, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 60
  [W10]::keybd_event(0x0D, 0, 2, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 2000
  $back = FindBtnByName $win '◀ 返回图库'
  Write-Output ('进入单图: ' + $(if ($back) { 'YES back.Top=' + [int]$back.Current.BoundingRectangle.Top } else { 'NO——Enter 未触发' }))
  if (-not $back) { Write-Output 'ABORT-NO-SINGLE'; exit 1 }

  # 截图 + 像素剖面：中心列从上往下找红图带（图片 bbox）与横条带
  $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp2 = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
  $g2 = [System.Drawing.Graphics]::FromImage($bmp2)
  $g2.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
  $bmp2.Save((Join-Path $shotDir 'wide-single.png'), [System.Drawing.Imaging.ImageFormat]::Png)
  $g2.Dispose()

  $cx = 40 + 750
  $redTop = -1; $redBottom = -1; $stripBottom = -1
  for ($y = 100; $y -lt 990; $y++) {
    $c = $bmp2.GetPixel($cx, $y)
    if ($redTop -lt 0 -and $c.R -gt 130 -and $c.G -lt 90 -and $c.B -lt 90) { $redTop = $y }
    if ($redTop -ge 0 -and $c.R -gt 130 -and $c.G -lt 90 -and $c.B -lt 90) { $redBottom = $y }
  }
  # 横条带（chrome 39,39,46 连续行，从 y100 往下第一段）
  $chromeRun = 0; $chromeTop = -1; $chromeBottom = -1
  for ($y = 100; $y -lt 400; $y++) {
    $c = $bmp2.GetPixel($cx, $y)
    $isChrome = ($c.R -ge 35 -and $c.R -le 45 -and $c.G -ge 35 -and $c.G -le 45)
    if ($isChrome) { if ($chromeTop -lt 0) { $chromeTop = $y }; $chromeRun++; $chromeBottom = $y } else { if ($chromeRun -gt 5) { }; $chromeRun = 0 }
  }
  Write-Output ('红图带: y=' + $redTop + '..' + $redBottom + '（高 ' + ($redBottom - $redTop) + 'px）')
  Write-Output ('chrome 连续段: y=' + $chromeTop + '..' + $chromeBottom)
  $gap = $redTop - $chromeBottom
  Write-Output ('横条底 到 图片顶 空隙 = ' + $gap + 'px（>30 即「返回悬空」视觉实锤）')
  $bmp2.Dispose()
  Write-Output 'DONE'
}
finally {
  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 600
  Move-Item $backup $settings -Force
  Write-Output 'SETTINGS-RESTORED'
}
