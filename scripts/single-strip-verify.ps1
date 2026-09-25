# single-strip-verify.ps1 - 走查5验证：单图模式顶部信息横条 + 缝隙透图检查
# 直开单图（-d <dir> -i N）+ 亮红测试图：顶带任何透明缝隙都会透出红色；断言顶带无红、横条文本、单图工具栏分簇
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W8 {
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[W8]::SetProcessDPIAware() | Out-Null

# qa/C-1 以脚本自身目录（scripts\）锚定仓库根推导 exe 路径，仓库克隆到任意路径/机器可用
$exe = Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = $settings + '.bak-s5'
# qa/B-1 备份前置守卫：残留备份 = 上次运行中途崩溃（finally 未执行）——先还原再重新备份，
# 防止把已被污染的 settings 当作新备份源、唯一好备份被删/覆盖
if (Test-Path $backup) {
  Move-Item $backup $settings -Force
  Write-Output ('STALE-BACKUP-RESTORED: ' + $backup + ' 已还原为 settings，随后重新备份')
}
Copy-Item $settings $backup -Force
$libDir = Join-Path $env:TEMP 'sv-verify-lib4'
$shotDir = Join-Path $env:TEMP 'sv-verify4'
if (Test-Path $shotDir) { Remove-Item $shotDir -Recurse -Force }
New-Item -ItemType Directory -Path $shotDir | Out-Null

function FindName($win, $name) {
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
  return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function FindBtn($win, $name) {
  $bt = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  $nm = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
  $and = New-Object System.Windows.Automation.AndCondition($bt, $nm)
  return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $and)
}
function Shot($name) {
  $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
  $bmp.Save((Join-Path $shotDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
}

try {
  if (Test-Path $libDir) { Remove-Item $libDir -Recurse -Force }
  New-Item -ItemType Directory -Path $libDir | Out-Null
  # 亮红测试图：任何透明缝隙都会透出红色
  foreach ($f in @('d1[废片].jpg', 'd2.jpg', 'd3[星标].jpg')) {
    $bmp = New-Object System.Drawing.Bitmap 600, 400
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 40, 40))
    $g.Dispose(); $bmp.Save((Join-Path $libDir $f), [System.Drawing.Imaging.ImageFormat]::Jpeg); $bmp.Dispose()
  }
  $json = Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json
  $json.LastLibraryRoot = $libDir
  $json.shortcuts = @()
  $json.tagGroups = @()
  $json | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding UTF8

  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 900
  Start-Process $exe -ArgumentList @('-d', $libDir, '-i', '1')
  Start-Sleep -Seconds 7
  $proc = Get-Process viewer -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  [W8]::MoveWindow($proc.MainWindowHandle, 40, 40, 1500, 950, $true) | Out-Null
  Start-Sleep -Milliseconds 1200

  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Simple Viewer')
  $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
  if (-not $win) { Write-Output 'NO-WINDOW'; exit 1 }

  Shot 's5-single.png'

  # 单图模式工具栏分簇 D1（此前 Enter 注入不稳从未自动断言过）
  # 返回图库断言用横条首元素「◀ 返回图库」（detail-view-return-entry-simplify 2026-09-25：
  # 工具栏同名按钮已删，单图返回入口唯一化为横条按钮）
  Write-Output '=== 1. 单图工具栏 D1 ==='
  foreach ($n in @('◀ 返回图库', '上一张', '下一张', '左旋', '右旋', '设置')) {
    $b = FindBtn $win $n
    Write-Output ($n + ': ' + $(if ($b -and -not $b.Current.IsOffscreen) { '可见' } else { '缺失/隐藏' }))
  }
  foreach ($n in @('全选', '删除')) {
    $b = FindBtn $win $n
    Write-Output ($n + '(应隐藏): ' + $(if ($b -and -not $b.Current.IsOffscreen) { 'FAIL-可见' } else { '隐藏OK' }))
  }

  # 2) 顶部横条文本（序号 + 显示名 d1）
  Write-Output '=== 2. 顶部横条 ==='
  $idxText = $null
  foreach ($n in @('1 / 3', '1/3', '1 / 3 张')) { $idxText = FindName $win $n; if ($idxText) { break } }
  Write-Output ('序号文本: ' + $(if ($idxText) { 'OK "' + $idxText.Current.Name + '"' } else { 'MISSING' }))
  $fn = FindName $win 'd1.jpg'
  Write-Output ('显示名 d1.jpg: ' + $(if ($fn) { 'OK' } else { 'MISSING' }))
  $back = $null
  foreach ($b in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))) {
    if ($b.Current.Name -like '*返回图库*') { $back = $b; break }
  }
  Write-Output ('返回图库浮层按钮: ' + $(if ($back) { 'OK rect=' + [int]$back.Current.BoundingRectangle.X + ',' + [int]$back.Current.BoundingRectangle.Y } else { 'MISSING' }))

  # 3) 缝隙透图检查：工具栏底到横条底之间（y=130..175）、横条带全宽（含左栏右缘交界，x=180 起）
  #    不得出现红色（R>150 且 G/B<110，排除浅色文字）；横条底边以下是图片本体显示区不扫
  Write-Output '=== 3. 顶带透红扫描 ==='
  $bmp = [System.Drawing.Bitmap]::FromFile((Join-Path $shotDir 's5-single.png'))
  $redHits = 0; $redAt = ''
  foreach ($y in 130..175) {
    foreach ($x in 180..1450 | Where-Object { $_ % 7 -eq 0 }) {
      $c = $bmp.GetPixel($x, $y)
      if ($c.R -gt 150 -and $c.G -lt 110 -and $c.B -lt 110) { $redHits++; $redAt += "($x,$y)" ; if ($redHits -gt 6) { break } }
    }
    if ($redHits -gt 6) { break }
  }
  $bmp.Dispose()
  if ($redHits -eq 0) { Write-Output 'NO-RED-SEAM: 顶带全宽无透图缝隙 ✓' } else { Write-Output ('RED-SEAM-FOUND: ' + $redHits + ' 处 ' + $redAt) }

  # 3b) 横条带内元素坐标（验证按钮与文本不再重叠：各元素 X 区间互不交叠）
  Write-Output '=== 3b. 横条带元素坐标 ==='
  $txtCond2 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
  foreach ($t in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $txtCond2)) {
    $r = $t.Current.BoundingRectangle
    if ($r.Y -ge 130 -and $r.Y -le 180 -and $r.Height -gt 0) {
      Write-Output ('  STRIP-TEXT "' + $t.Current.Name + '" @ ' + [int]$r.X + '..' + [int]($r.X + $r.Width) + ' y=' + [int]$r.Y)
    }
  }
  foreach ($b in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))) {
    $r = $b.Current.BoundingRectangle
    if ($r.Y -ge 130 -and $r.Y -le 180 -and $r.Height -gt 0) {
      Write-Output ('  STRIP-BTN "' + $b.Current.Name + '" @ ' + [int]$r.X + '..' + [int]($r.X + $r.Width) + ' y=' + [int]$r.Y)
    }
  }

  # 4) 剖面：x=700 顶带垂直色带
  $bmp2 = [System.Drawing.Bitmap]::FromFile((Join-Path $shotDir 's5-single.png'))
  foreach ($y in 135..190 | Where-Object { $_ % 5 -eq 0 }) {
    $c = $bmp2.GetPixel(700, $y)
    Write-Output ('PROFILE x=700 y=' + $y + ' RGB=' + $c.R + ',' + $c.G + ',' + $c.B)
  }
  $bmp2.Dispose()
  Write-Output 'DONE'
}
finally {
  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 600
  Move-Item $backup $settings -Force
  Write-Output 'SETTINGS-RESTORED'
}
