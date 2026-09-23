# repro-strip-float.ps1 - 复现「图库有筛选时详情页返回悬空」
# 流程：图库 6 张（2 张带标签）→ UIA 点左栏标签激活筛选 → raw 双击首卡进单图（DoubleTapped 只认真实手势）
#       → 采 UIA 几何（返回按钮/工具栏/图片）+ 截图，对照无筛选组。
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W9 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint data, UIntPtr extra);
}
"@
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[W9]::SetProcessDPIAware() | Out-Null

$exe = Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = $settings + '.bak-sf'
if (Test-Path $backup) {
  Move-Item $backup $settings -Force
  Write-Output ('STALE-BACKUP-RESTORED: ' + $backup)
}
Copy-Item $settings $backup -Force
$libDir = Join-Path $env:TEMP 'sv-verify-lib-sf'
$shotDir = Join-Path $env:TEMP 'sv-verify-sf'
if (Test-Path $shotDir) { Remove-Item $shotDir -Recurse -Force }
New-Item -ItemType Directory -Path $shotDir | Out-Null

function FindBtnByName($win, $name) {
  $bt = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  $nm = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
  $and = New-Object System.Windows.Automation.AndCondition($bt, $nm)
  return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $and)
}
function InvokeEl($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Rect($el) {
  $r = $el.Current.BoundingRectangle
  return ('L' + [int]$r.Left + ' T' + [int]$r.Top + ' R' + [int]$r.Right + ' B' + [int]$r.Bottom)
}
function DblClick($el) {
  $r = $el.Current.BoundingRectangle
  $cx = [int](($r.Left + $r.Right) / 2); $cy = [int](($r.Top + $r.Bottom) / 2)
  [W9]::SetCursorPos($cx, $cy) | Out-Null
  Start-Sleep -Milliseconds 80
  [W9]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [W9]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 60
  [W9]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [W9]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 150
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
  # 6 张：b1/b2 带「风景」，其余无标签；尺寸偏小放大 fit 空白差异
  $i = 1
  foreach ($f in @('b1[风景].jpg', 'b2[风景].jpg', 'b3.jpg', 'b4.jpg', 'b5.jpg', 'b6.jpg')) {
    $bmp = New-Object System.Drawing.Bitmap 200, 150
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb((60 + $i * 15), 90, 120))
    $g.Dispose(); $bmp.Save((Join-Path $libDir $f), [System.Drawing.Imaging.ImageFormat]::Jpeg); $bmp.Dispose()
    $i++
  }
  $json = Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json
  $json.LastLibraryRoot = $libDir
  $json.shortcuts = @()
  $json.tagGroups = @(
    [PSCustomObject]@{ id = 'vg1'; name = '主题'; exclusive = $false; tags = @([PSCustomObject]@{ id = 'vt1'; name = '风景' }) }
  )
  $json | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding UTF8

  foreach ($mode in @('filtered', 'plain')) {
    taskkill /IM viewer.exe /F 2>$null | Out-Null
    Start-Sleep -Milliseconds 900
    Start-Process $exe
    Start-Sleep -Seconds 9
    $proc = Get-Process viewer -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    [W9]::MoveWindow($proc.MainWindowHandle, 40, 40, 1500, 950, $true) | Out-Null
    [W9]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 800
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Simple Viewer')
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if (-not $win) { Write-Output 'NO-WINDOW'; exit 1 }

    if ($mode -eq 'filtered') {
      $fj = FindBtnByName $win '风景'
      if ($fj) { InvokeEl $fj; Start-Sleep -Milliseconds 1200; Write-Output ('[' + $mode + '] 筛选已激活') }
      else { Write-Output ('[' + $mode + '] TAGROW-MISSING') }
    }

    # raw 双击第一张卡片进单图
    $card = FindBtnByName $win 'b1.jpg'
    if (-not $card) { $card = FindBtnByName $win 'b3.jpg' }
    if (-not $card) { Write-Output ('[' + $mode + '] CARD-MISSING'); continue }
    Write-Output ('[' + $mode + '] card=' + (Rect $card))
    DblClick $card
    Start-Sleep -Milliseconds 1800

    $back = FindBtnByName $win '◀ 返回图库'
    $sel = FindBtnByName $win '全选'
    $img = $null
    $imgCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Image)
    $img = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $imgCond)
    Write-Output ('[' + $mode + '] back=' + $(if ($back) { Rect $back } else { 'MISSING' }))
    Write-Output ('[' + $mode + '] sel(工具栏)=' + $(if ($sel) { Rect $sel } else { 'MISSING(IsOffscreen?)' }))
    Write-Output ('[' + $mode + '] image=' + $(if ($img) { Rect $img } else { 'MISSING' }))
    Shot ('strip-' + $mode + '.png')
    Write-Output ('[' + $mode + '] shot=strip-' + $mode + '.png')
  }
  Write-Output 'DONE'
}
finally {
  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 600
  Move-Item $backup $settings -Force
  Write-Output 'SETTINGS-RESTORED'
}
