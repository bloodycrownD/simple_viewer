# walkthrough2-verify.ps1 - 走查2两问题验证
# ① 右栏铺满：面板上下段像素采样应同色（修复前下半段露窗口底色）
# ② 选择回归排查：UIA 无键盘路径连点两张卡 → 应保持「已选 1 张」（单选重置）
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W7 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[W7]::SetProcessDPIAware() | Out-Null

# qa/C-1 以脚本自身目录（scripts\）锚定仓库根推导 exe 路径，仓库克隆到任意路径/机器可用
$exe = Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = $settings + '.bak-wt2'
# qa/B-1 备份前置守卫：残留备份 = 上次运行中途崩溃（finally 未执行）——先还原再重新备份，
# 防止把已被污染的 settings 当作新备份源、唯一好备份被删/覆盖
if (Test-Path $backup) {
  Move-Item $backup $settings -Force
  Write-Output ('STALE-BACKUP-RESTORED: ' + $backup + ' 已还原为 settings，随后重新备份')
}
Copy-Item $settings $backup -Force
$libDir = Join-Path $env:TEMP 'sv-verify-lib3'
$shotDir = Join-Path $env:TEMP 'sv-verify3'
if (Test-Path $shotDir) { Remove-Item $shotDir -Recurse -Force }
New-Item -ItemType Directory -Path $shotDir | Out-Null

function FindName($win, $name) {
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
  return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function InvokeEl($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function FindId($win, $id) {
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function AncestorButton($el) {
  $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
  $p = $el
  while ($p) {
    $p = $walker.GetParent($p)
    if ($p -and $p.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) { return $p }
  }
  return $null
}
function SelectCount($win) {
  foreach ($n in @('已选 0 张','已选 1 张','已选 2 张','已选 3 张','已选 4 张')) {
    $t = FindName $win $n
    if ($t) { return $n }
  }
  return 'UNKNOWN'
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
  foreach ($f in @('c1[废片].jpg', 'c2[废片].jpg', 'c3[星标].jpg', 'c4.jpg')) {
    $bmp = New-Object System.Drawing.Bitmap 160, 120
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(70, 100, 130))
    $g.Dispose(); $bmp.Save((Join-Path $libDir $f), [System.Drawing.Imaging.ImageFormat]::Jpeg); $bmp.Dispose()
  }
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
  [W7]::MoveWindow($proc.MainWindowHandle, 40, 40, 1500, 950, $true) | Out-Null
  Start-Sleep -Milliseconds 800
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Simple Viewer')
  $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
  if (-not $win) { Write-Output 'NO-WINDOW'; exit 1 }

  # ── 1. 右栏铺满验证：面板上/下段像素应同色（窗口 40,40 1500x950；面板 x≈1120-1540） ──
  Shot 'w2-panel.png'
  $bmp = [System.Drawing.Bitmap]::FromFile((Join-Path $shotDir 'w2-panel.png'))
  $pts = @(@(700, 25), @(1300, 300), @(1300, 700), @(1300, 900), @(150, 400))
  foreach ($pt in $pts) {
    $c = $bmp.GetPixel($pt[0], $pt[1])
    Write-Output ('PIXEL @' + $pt[0] + ',' + $pt[1] + ' RGB=' + $c.R + ',' + $c.G + ',' + $c.B)
  }
  $bmp.Dispose()
  # 上(300) vs 下(900) 色差
  $bmp2 = [System.Drawing.Bitmap]::FromFile((Join-Path $shotDir 'w2-panel.png'))
  $c1 = $bmp2.GetPixel(1300, 300); $c2p = $bmp2.GetPixel(1300, 900)
  $dist = [Math]::Abs($c1.R - $c2p.R) + [Math]::Abs($c1.G - $c2p.G) + [Math]::Abs($c1.B - $c2p.B)
  Write-Output ('PANEL-TOP-BOTTOM-DIFF=' + $dist + '（<30 视为统一；修复前下半段为窗口底色差值大）')
  $bmp2.Dispose()

  # ── 2. 选择行为验证：UIA 连点两张卡（无键盘路径；按尺寸定位画布区大按钮=卡片） ──
  $btCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  $allBtns = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btCond)
  $cards = @()
  foreach ($b in $allBtns) {
    $r = $b.Current.BoundingRectangle
    if ($r.Width -gt 80 -and $r.Height -gt 90 -and $r.X -gt 500 -and $r.X -lt 1100) { $cards += ,@($r.Y, $b) }
  }
  $cards = $cards | Sort-Object { $_[0] }
  Write-Output ('CARDS-FOUND: ' + $cards.Count)
  if ($cards.Count -ge 2) {
    InvokeEl $cards[0][1]
    Start-Sleep -Milliseconds 900
    Write-Output ('CLICK-card1 -> ' + (SelectCount $win))
    # chip 纵向位置断言：应在面板顶部区（标题+「标签」节标题下方），修复前浮在面板中部
    $chip = FindId $win 'SelectionChip_废片'
    if ($chip) {
      $cr = $chip.Current.BoundingRectangle
      $verdict = if ($cr.Y -lt 450) { 'OK-顶部区' } else { 'FAIL-仍浮中部' }
      Write-Output ('CHIP-POS: y=' + [int]$cr.Y + ' x=' + [int]$cr.X + ' ' + $verdict)
    } else { Write-Output 'CHIP-废片-MISSING' }
    InvokeEl $cards[1][1]
    Start-Sleep -Milliseconds 900
    $after = SelectCount $win
    Write-Output ('CLICK-card2 -> ' + $after + '（单选重置口径应为 已选 1 张；若 已选 2 张=回归实锤）')
    Shot 'w2-after-two-clicks.png'
  } else { Write-Output 'CARDS-INSUFFICIENT' }
  Write-Output 'DONE'
}
finally {
  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 600
  Move-Item $backup $settings -Force
  Write-Output 'SETTINGS-RESTORED'
}
