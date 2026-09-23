# repro-strip-float5.ps1 - 「InfoBar 回执弹出→TopChromeHeight 增大→横条下移」机制验证
# 手法：锁住一张图（FileShare.None 独占句柄）→ 图库批量打标该文件必失败 → 失败回执 InfoBar 弹出
#       → 进单图记返回按钮 T → 关 InfoBar 再记 T（不回落=SizeChanged 不触发实锤；回落=常驻问题）
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W15 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
"@
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[W15]::SetProcessDPIAware() | Out-Null

$exe = Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = $settings + '.bak-sf5'
if (Test-Path $backup) {
  Move-Item $backup $settings -Force
  Write-Output ('STALE-BACKUP-RESTORED: ' + $backup)
}
Copy-Item $settings $backup -Force
$libDir = Join-Path $env:TEMP 'sv-verify-lib-sf5'

function FindBtnByName($win, $name) {
  $bt = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  $nm = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
  $and = New-Object System.Windows.Automation.AndCondition($bt, $nm)
  return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $and)
}
function InvokeEl($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function BackT($win, $tag) {
  $b = FindBtnByName $win '◀ 返回图库'
  if (-not $b) { Write-Output ($tag + ' back=MISSING'); return }
  Write-Output ($tag + ' back T=' + [int]$b.Current.BoundingRectangle.Top + ' enabled=' + $b.Current.IsEnabled)
}

try {
  if (Test-Path $libDir) { Remove-Item $libDir -Recurse -Force }
  New-Item -ItemType Directory -Path $libDir | Out-Null
  foreach ($f in @('a1[风景].jpg', 'a2[风景].jpg', 'a3[风景].jpg')) {
    $bmp = New-Object System.Drawing.Bitmap 450, 600
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(80, 100, 130))
    $g.Dispose(); $bmp.Save((Join-Path $libDir $f), [System.Drawing.Imaging.ImageFormat]::Jpeg); $bmp.Dispose()
  }
  $json = Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json
  $json.LastLibraryRoot = $libDir
  $json.shortcuts = @()
  $json.tagGroups = @(
    [PSCustomObject]@{ id = 'vg1'; name = '主题'; exclusive = $false; tags = @([PSCustomObject]@{ id = 'vt1'; name = '风景' }, [PSCustomObject]@{ id = 'vt2'; name = '星标' }) }
  )
  $json | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding UTF8

  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 900
  Start-Process $exe
  Start-Sleep -Seconds 9
  $proc = Get-Process viewer -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  [W15]::MoveWindow($proc.MainWindowHandle, 40, 40, 1500, 950, $true) | Out-Null
  Start-Sleep -Milliseconds 800
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Simple Viewer')
  $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
  if (-not $win) { Write-Output 'NO-WINDOW'; exit 1 }

  # 激活筛选
  $fj = FindBtnByName $win '风景'
  if ($fj) { InvokeEl $fj; Start-Sleep -Milliseconds 1200; Write-Output '筛选已激活' }

  # 基线：进单图记 T
  $card = FindBtnByName $win 'a1.jpg'
  $r0 = $card.Current.BoundingRectangle
  [W15]::SetCursorPos([int](($r0.Left + $r0.Right) / 2), [int](($r0.Top + $r0.Bottom) / 2)) | Out-Null
  Start-Sleep -Milliseconds 80
  [W15]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [W15]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 500
  [W15]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
  [W15]::keybd_event(0x0D, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60; [W15]::keybd_event(0x0D, 0, 2, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 1800
  BackT $win '[基线] 无回执时'
  # 回图库
  $bk = FindBtnByName $win '◀ 返回图库'; InvokeEl $bk; Start-Sleep -Milliseconds 1500

  # 锁 a2 → 批量打标必失败 → 失败回执 InfoBar 弹出
  $lockStream = [System.IO.File]::Open((Join-Path $libDir 'a2[风景].jpg'), [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
  Write-Output 'a2 已锁定（独占句柄）'

  # 全选 → ＋ 添加「星标」（a1/a3 成功、a2 失败）
  $sa = FindBtnByName $win '全选'
  if ($sa) { InvokeEl $sa; Start-Sleep -Milliseconds 1200 }
  $add = FindBtnByName $win '＋'
  if (-not $add) { $add = FindBtnByName $win '添加标签' }
  InvokeEl $add
  Start-Sleep -Milliseconds 1200
  $entry = FindBtnByName $win '星标'
  if ($entry) { InvokeEl $entry; Start-Sleep -Milliseconds 2500; Write-Output '批量打标已执行（预期 a2 失败）' }

  # InfoBar 是否在？（找关闭按钮/文本）
  $infoClose = FindBtnByName $win 'Close'
  $tbCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
  $allTb = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tbCond)
  foreach ($t in $allTb) {
    $n = $t.Current.Name
    if ($n -match '失败|移除|打标|标签') { Write-Output ('TEXT [' + $n.Substring(0, [Math]::Min(60, $n.Length)) + '] T=' + [int]$t.Current.BoundingRectangle.Top) }
  }

  # 关键观测：InfoBar 弹出状态下进单图 → back T 是否下移
  $card = FindBtnByName $win 'a1.jpg'
  if ($card) {
    $r0 = $card.Current.BoundingRectangle
    [W15]::SetCursorPos([int](($r0.Left + $r0.Right) / 2), [int](($r0.Top + $r0.Bottom) / 2)) | Out-Null
    Start-Sleep -Milliseconds 80
    [W15]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [W15]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 500
    [W15]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
    [W15]::keybd_event(0x0D, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60; [W15]::keybd_event(0x0D, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 1800
    BackT $win '[InfoBar 开] 进单图后'
    $bk = FindBtnByName $win '◀ 返回图库'
    if ($bk) { InvokeEl $bk; Start-Sleep -Milliseconds 1500 }
  }

  # 解锁 → 回图库关 InfoBar（若可关）→ 再进单图观测是否回落
  $lockStream.Dispose()
  Write-Output 'a2 已解锁'
  $infoClose = FindBtnByName $win 'Close'
  if ($infoClose) { InvokeEl $infoClose; Start-Sleep -Milliseconds 1000; Write-Output 'InfoBar 已关闭' }
  else { Write-Output 'InfoBar 无关闭按钮（可能已自动关）' }

  $card = FindBtnByName $win 'a1.jpg'
  if ($card) {
    $r0 = $card.Current.BoundingRectangle
    [W15]::SetCursorPos([int](($r0.Left + $r0.Right) / 2), [int](($r0.Top + $r0.Bottom) / 2)) | Out-Null
    Start-Sleep -Milliseconds 80
    [W15]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [W15]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 500
    [W15]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
    [W15]::keybd_event(0x0D, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60; [W15]::keybd_event(0x0D, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 1800
    BackT $win '[InfoBar 关后] 进单图后'
  }
  Write-Output 'DONE'
}
finally {
  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 600
  Move-Item $backup $settings -Force
  Write-Output 'SETTINGS-RESTORED'
}
