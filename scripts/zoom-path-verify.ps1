# zoom-path-verify.ps1 - 换源路径（FromLoadedImageAsync SoftwareBitmapSource）端到端：
# -d 直开 → 红图可见（像素）→ UIA 翻页×2 → 每页红图可见 + 横条序号跟随
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct RECT { public int Left, Top, Right, Bottom; }
public class W9 {
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[W9]::SetProcessDPIAware() | Out-Null

$libDir = Join-Path $env:TEMP 'sv-zoom-lib'
$shotDir = Join-Path $env:TEMP 'sv-zoom'
if (Test-Path $shotDir) { Remove-Item $shotDir -Recurse -Force }
New-Item -ItemType Directory -Path $shotDir | Out-Null
# qa/C-1 以脚本自身目录（scripts\）锚定仓库根推导 exe 路径，仓库克隆到任意路径/机器可用
$exe = Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = $settings + '.bak-zm'
# qa/B-1 备份前置守卫：残留备份 = 上次运行中途崩溃（finally 未执行）——先还原再重新备份，
# 防止把已被污染的 settings 当作新备份源、唯一好备份被删/覆盖
if (Test-Path $backup) {
  Move-Item $backup $settings -Force
  Write-Output ('STALE-BACKUP-RESTORED: ' + $backup + ' 已还原为 settings，随后重新备份')
}
Copy-Item $settings $backup -Force

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
  # 屏幕外可用的截图：PrintWindow 取窗口内容（不依赖窗口在屏内、不被遮挡），再按窗口屏幕原点
  # 贴回全屏尺寸画布——采样坐标口径与旧 CopyFromScreen 版完全一致（绝对屏幕坐标）。
  $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::Black)
  $wr = New-Object RECT; [void][W9]::GetWindowRect($hwnd, [ref]$wr)
  $wb = New-Object System.Drawing.Bitmap ($wr.Right - $wr.Left), ($wr.Bottom - $wr.Top)
  $wg = [System.Drawing.Graphics]::FromImage($wb)
  $dc = $wg.GetHdc(); [void][W9]::PrintWindow($hwnd, $dc, 2); $wg.ReleaseHdc($dc)
  # 贴到画布 (40,40)：下方像素采样用字面量坐标（600,400 = 1200x800 窗口中心），口径＝旧版
  # CopyFromScreen「窗口在 (40,40)」的假设；真实窗口现在屏幕外也无妨。
  $g.DrawImage($wb, 40, 40)
  $bmp.Save((Join-Path $shotDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
  $script:lastShot = (Join-Path $shotDir $name)
  $wg.Dispose(); $wb.Dispose(); $g.Dispose(); $bmp.Dispose()
}
function RedAtImageCenter {
  # 最新截图里取 UIA 图片元素中心处像素是否红。截图已贴到画布 (40,40)，故
  # 画布坐标 = 屏幕坐标 - 窗口原点 + (40,40)。
  $imgCond = New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Image)
  $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $imgCond)
  if (-not $el) { return 'NO-IMAGE-ELEMENT' }
  $r = $el.Current.BoundingRectangle
  if ([double]::IsNaN($r.Left) -or $r.Width -le 0) { return 'IMAGE-BOX-EMPTY' }
  $wr = New-Object RECT; [void][W9]::GetWindowRect($hwnd, [ref]$wr)
  $cx = [int](($r.Left + $r.Right) / 2) - $wr.Left + 40
  $cy = [int](($r.Top + $r.Bottom) / 2) - $wr.Top + 40
  $bmp = [System.Drawing.Bitmap]::FromFile($script:lastShot)
  $c = $bmp.GetPixel($cx, $cy); $bmp.Dispose()
  $isRed = ($c.R -gt 150 -and $c.G -lt 110 -and $c.B -lt 110)
  return ('{0} img-box={1}x{2} @({3},{4})={5},{6},{7}' -f $isRed, [int]$r.Width, [int]$r.Height, $cx, $cy, $c.R, $c.G, $c.B)
}
function RedAt($png, $x, $y) {
  $bmp = [System.Drawing.Bitmap]::FromFile($png)
  $c = $bmp.GetPixel($x, $y); $bmp.Dispose()
  return ($c.R -gt 150 -and $c.G -lt 110 -and $c.B -lt 110)
}

try {
  if (Test-Path $libDir) { Remove-Item $libDir -Recurse -Force }
  New-Item -ItemType Directory -Path $libDir | Out-Null
  foreach ($f in @('z1.jpg', 'z2.jpg', 'z3.jpg')) {
    $bmp = New-Object System.Drawing.Bitmap 3000, 2000
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
  Start-Sleep -Milliseconds 800
  Start-Process $exe -ArgumentList @('-d', $libDir, '-i', '1')
  Start-Sleep -Seconds 7
  $proc = Get-Process viewer -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  $hwnd = $proc.MainWindowHandle
  # 屏幕外定位（RULE：UI 验证不抢焦点；UIA Invoke 与 PrintWindow 不要求窗口可见）
  [W9]::MoveWindow($hwnd, ([W9]::GetSystemMetrics(0) + 2000), 40, 1200, 800, $true) | Out-Null
  Start-Sleep -Milliseconds 1000

  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Simple Viewer')
  $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
  if (-not $win) { Write-Output 'NO-WINDOW'; exit 1 }

  # 页1：图片渲染（红）+ 横条序号 1 / 3
  # 采样点 = UIA 图片元素中心（不再写死 (600,400)：2026-09-25 起单图按「可见区」适配，
  # 可视区随左栏/右栏信息面板收展变化，写死坐标会随面板展开而落到图外→假红失败）
  Shot 'z-page1.png'
  $red1 = RedAtImageCenter
  $idx1 = FindName $win '1 / 3'
  Write-Output ('PAGE1: red=' + $red1 + '  index-text=' + $(if ($idx1) { 'OK' } else { 'MISSING' }))

  foreach ($n in @('2 / 3', '3 / 3')) {
    $next = FindBtn $win '下一张'
    if (-not $next) { Write-Output 'NEXT-BTN-MISSING'; break }
    $next.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 1800
    Shot ('z-' + $n.Replace(' / ', '-') + '.png')
    $red = RedAtImageCenter
    $idx = FindName $win $n
    Write-Output ($n + ': red=' + $red + '  index=' + $(if ($idx) { 'OK' } else { 'MISSING' }))
  }
  Write-Output 'DONE'
}
finally {
  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 500
  Move-Item $backup $settings -Force
  Write-Output 'SETTINGS-RESTORED'
}
