# sidebar-select-verify.ps1 - 左栏标签 Explorer 筛选心智验证（2026-09-24 恢复 09-19 用户拍板）
# 期望：无修饰点击 = 单选替换（当前唯一激活该标签时再点取消）；Ctrl+点击 = 加减选。
# 注入方式：UIA Invoke 走无修饰分支（键盘态无 Ctrl）；Ctrl 分支用 raw 键鼠注入
#           （keybd_event + mouse_event；注入前 SetForegroundWindow 激活；Ctrl 收尾必补偿 keyup——RULE 铁律）。
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W8 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
"@
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[W8]::SetProcessDPIAware() | Out-Null

# qa/C-1：以脚本自身目录锚定仓库根推导 exe，任意机器/路径可跑
$exe = Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = $settings + '.bak-sbs'
# qa/B-1 备份前置守卫：残留备份 = 上次中途崩溃——先还原再重新备份，防好备份被毁
if (Test-Path $backup) {
  Move-Item $backup $settings -Force
  Write-Output ('STALE-BACKUP-RESTORED: ' + $backup + ' 已还原为 settings，随后重新备份')
}
Copy-Item $settings $backup -Force
$libDir = Join-Path $env:TEMP 'sv-verify-lib-sbs'

function FindBtnByName($win, $name) {
  $bt = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  $nm = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
  $and = New-Object System.Windows.Automation.AndCondition($bt, $nm)
  return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $and)
}
function InvokeEl($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function CountCards($win) {
  # 按卡片显示名定位（DisplayName = 基名+扩展名、标签段已剥离，GalleryItem 口径）；
  # 筛选命中数 = 存在的卡片数
  $n = 0
  foreach ($f in @('a1.jpg', 'a2.jpg', 'a3.jpg')) {
    if (FindBtnByName $win $f) { $n++ }
  }
  return $n
}
function CtrlClick($el) {
  # raw Ctrl+点击：先激活窗口；Ctrl 收尾补偿 keyup（KEYEVENTF_KEYUP=0x2）
  $r = $el.Current.BoundingRectangle
  $cx = [int](($r.Left + $r.Right) / 2)
  $cy = [int](($r.Top + $r.Bottom) / 2)
  [W8]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 80
  [W8]::SetCursorPos($cx, $cy) | Out-Null
  Start-Sleep -Milliseconds 80
  [W8]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
  [W8]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 150
  [W8]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
}

try {
  if (Test-Path $libDir) { Remove-Item $libDir -Recurse -Force }
  New-Item -ItemType Directory -Path $libDir | Out-Null
  # 3 张：风景=2（a1,a3）、星标=2（a2,a3）、全量=3
  foreach ($f in @('a1[风景].jpg', 'a2[星标].jpg', 'a3[风景 星标].jpg')) {
    $bmp = New-Object System.Drawing.Bitmap 160, 120
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(90, 110, 140))
    $g.Dispose(); $bmp.Save((Join-Path $libDir $f), [System.Drawing.Imaging.ImageFormat]::Jpeg); $bmp.Dispose()
  }
  $json = Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json
  $json.LastLibraryRoot = $libDir
  $json.shortcuts = @()
  $json.tagGroups = @(
    [PSCustomObject]@{ id = 'vg1'; name = '主题'; exclusive = $false; tags = @([PSCustomObject]@{ id = 'vt1'; name = '风景' }) },
    [PSCustomObject]@{ id = 'vg2'; name = '评价'; exclusive = $false; tags = @([PSCustomObject]@{ id = 'vt2'; name = '星标' }) }
  )
  $json | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding UTF8

  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 900
  Start-Process $exe
  Start-Sleep -Seconds 9
  $proc = Get-Process viewer -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  [W8]::MoveWindow($proc.MainWindowHandle, 40, 40, 1500, 950, $true) | Out-Null
  [W8]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
  Start-Sleep -Milliseconds 800
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Simple Viewer')
  $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
  if (-not $win) { Write-Output 'NO-WINDOW'; exit 1 }

  Write-Output ('BASE-COUNT: ' + (CountCards $win) + '（期望 3）')

  # ── 1. 无修饰点击「风景」：单选替换 → 命中 2 ──
  $fj = FindBtnByName $win '风景'
  if (-not $fj) { Write-Output 'TAGROW-风景-MISSING'; exit 1 }
  InvokeEl $fj
  Start-Sleep -Milliseconds 1200
  Write-Output ('CLICK-风景 -> ' + (CountCards $win) + '（期望 2：单选替换）')

  # ── 2. 无修饰点击「星标」：替换非叠加（QuickAdd 回归会=3）→ 命中 2 ──
  $xb = FindBtnByName $win '星标'
  if (-not $xb) { Write-Output 'TAGROW-星标-MISSING'; exit 1 }
  InvokeEl $xb
  Start-Sleep -Milliseconds 1200
  Write-Output ('CLICK-星标 -> ' + (CountCards $win) + '（期望 2：替换；3=QuickAdd 回归实锤）')

  # ── 3. raw Ctrl+点击「风景」：加减选 → 星标 OR 风景 命中 3 ──
  $fj = FindBtnByName $win '风景'
  CtrlClick $fj
  Start-Sleep -Milliseconds 1200
  Write-Output ('CTRL-CLICK-风景 -> ' + (CountCards $win) + '（期望 3：加选 OR）')

  # ── 4. raw Ctrl+点击「星标」：减选 → 剩风景 命中 2 ──
  $xb = FindBtnByName $win '星标'
  CtrlClick $xb
  Start-Sleep -Milliseconds 1200
  Write-Output ('CTRL-CLICK-星标 -> ' + (CountCards $win) + '（期望 2：减选）')

  # ── 5. 无修饰点击「风景」：唯一激活再点取消 → 全量 3 ──
  $fj = FindBtnByName $win '风景'
  InvokeEl $fj
  Start-Sleep -Milliseconds 1200
  Write-Output ('CLICK-风景(唯一激活) -> ' + (CountCards $win) + '（期望 3：再点取消）')

  Write-Output 'DONE'
}
finally {
  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 600
  Move-Item $backup $settings -Force
  Write-Output 'SETTINGS-RESTORED'
}
