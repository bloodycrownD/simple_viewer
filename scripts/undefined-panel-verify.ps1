# undefined-panel-verify.ps1 - batch-tag-management Step 3/4 实机自检
# 未定义标签区 + 图库右栏（选中集并集 + ✕ 批量移除端到端）
# 流程：备份 settings → 临时图库(带未定义标签) → 受控 tagGroups → 启动 → UIA 断言 → Ctrl+A → ✕ 移除 → 截图 → 还原
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W5 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
}
"@
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[W5]::SetProcessDPIAware() | Out-Null

$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = $settings + '.bak-undef'
if (Test-Path $backup) { Remove-Item $backup -Force }
Copy-Item $settings $backup -Force

$libDir = Join-Path $env:TEMP 'sv-verify-lib'
$shotDir = Join-Path $env:TEMP 'sv-verify'
if (Test-Path $shotDir) { Remove-Item $shotDir -Recurse -Force }
New-Item -ItemType Directory -Path $shotDir | Out-Null

function FindId($win, $id) {
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
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
  # 1) 临时图库：6 张小图，废片x3 星标x2 已修x1，一张无标签
  if (Test-Path $libDir) { Remove-Item $libDir -Recurse -Force }
  New-Item -ItemType Directory -Path $libDir | Out-Null
  $files = @('a1[废片].jpg', 'a2[废片 星标].jpg', 'a3[星标].jpg', 'a4[已修].jpg', 'a5.jpg', 'a6[废片].jpg')
  foreach ($f in $files) {
    $bmp = New-Object System.Drawing.Bitmap 160, 120
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(60, 80, 120))
    $g.Dispose()
    $bmp.Save((Join-Path $libDir $f), [System.Drawing.Imaging.ImageFormat]::Jpeg)
    $bmp.Dispose()
  }

  # 2) 受控 settings：LastLibraryRoot 指临时库；tagGroups 仅一组主题(风景/城市)——废片/星标/已修 全部未定义
  #    shortcuts 清空：用户真实配置 Ctrl+A=rotateLeft 会经 TryMatch 优先劫持，Ctrl+A 全选接管分支收不到（产品设计，非 bug）
  $json = Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json
  $json.LastLibraryRoot = $libDir
  $json.shortcuts = @()
  $json.tagGroups = @(
    [PSCustomObject]@{ id = 'vg1'; name = '主题'; exclusive = $false; tags = @(
      [PSCustomObject]@{ id = 'vt1'; name = '风景' },
      [PSCustomObject]@{ id = 'vt2'; name = '城市' }
    ) }
  )
  $json | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding UTF8

  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 900
  Start-Process 'D:\Dev\Python\simple_viewer\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
  Start-Sleep -Seconds 9
  $proc = Get-Process viewer -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  [W5]::MoveWindow($proc.MainWindowHandle, 40, 40, 1400, 950, $true) | Out-Null
  Start-Sleep -Milliseconds 500
  $wsh = New-Object -ComObject WScript.Shell
  $null = $wsh.AppActivate($proc.Id)
  Start-Sleep -Milliseconds 800

  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Simple Viewer')
  $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
  if (-not $win) { Write-Output 'NO-WINDOW'; exit 1 }

  # 3) 断言一：未定义标签区
  $hdr = FindId $win 'UndefinedTagsHeader'
  if ($hdr) {
    $r = $hdr.Current.BoundingRectangle
    Write-Output ('UNDEF-HEADER-OK: text="' + $hdr.Current.Name + '" rect=' + [int]$r.X + ',' + [int]$r.Y + ' ' + [int]$r.Width + 'x' + [int]$r.Height)
  } else { Write-Output 'UNDEF-HEADER-MISSING' }
  foreach ($t in @('废片', '星标', '已修')) {
    $chip = FindId $win ('UndefinedChip_' + $t)
    if ($chip) {
      $r = $chip.Current.BoundingRectangle
      Write-Output ('UNDEF-CHIP-OK: ' + $t + ' name="' + $chip.Current.Name + '" rect=' + [int]$r.X + ',' + [int]$r.Y + ' ' + [int]$r.Width + 'x' + [int]$r.Height)
    } else { Write-Output ('UNDEF-CHIP-MISSING: ' + $t) }
  }

  # 4) 断言二：右栏展开态（默认展开；Border 无 automation peer，用收起按钮与标题文本判定）
  $collapse = FindId $win 'GallerySelectionPanelCollapse'
  if ($collapse) {
    $r = $collapse.Current.BoundingRectangle
    Write-Output ('GALLERY-PANEL-OK(collapse-btn): rect=' + [int]$r.X + ',' + [int]$r.Y + ' ' + [int]$r.Width + 'x' + [int]$r.Height + ' (280dip@150%=420phys, 右缘)')
  } else { Write-Output 'GALLERY-PANEL-MISSING(collapse-btn)' }
  $titleCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '已选 0 张')
  $title0 = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $titleCond)
  if ($title0) { Write-Output 'GALLERY-TITLE-OK: 已选 0 张' } else { Write-Output 'GALLERY-TITLE-MISSING: 已选 0 张' }

  # 5) 全选 → 并集 chip + 标题（UIA Invoke 工具栏「全选」按钮驱动——与 Ctrl+A 同管线 SelectAllCards；
  #    不用 SendKeys：中文 IME 环境键盘注入可能被输入法吞掉）
  $btnCond2 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  $nameCond2 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '全选')
  $andCond2 = New-Object System.Windows.Automation.AndCondition($btnCond2, $nameCond2)
  $selectAllBtn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $andCond2)
  if ($selectAllBtn) {
    $selectAllBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output 'SELECT-ALL-INVOKED'
  } else { Write-Output 'SELECT-ALL-BTN-MISSING' }
  Start-Sleep -Milliseconds 1500
  $titleCond6 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '已选 6 张')
  $title6 = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $titleCond6)
  if ($title6) { Write-Output 'GALLERY-TITLE-6-OK: 已选 6 张' } else { Write-Output 'GALLERY-TITLE-6-MISSING' }
  foreach ($t in @('废片', '星标', '已修')) {
    $c2 = FindId $win ('SelectionChip_' + $t)
    if ($c2) {
      $r = $c2.Current.BoundingRectangle
      Write-Output ('SELECT-CHIP-OK: ' + $t + ' name="' + $c2.Current.Name + '" rect=' + [int]$r.X + ',' + [int]$r.Y + ' ' + [int]$r.Width + 'x' + [int]$r.Height)
    } else { Write-Output ('SELECT-CHIP-MISSING: ' + $t) }
  }
  Shot 'shot-1-select-all.png'

  # 6) ✕ 批量移除端到端：移除「废片」（期望 3 个文件改名、chip 消失）
  $chipDel = FindId $win 'SelectionChip_废片'
  if ($chipDel) {
    $btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $xb = $chipDel.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
    if ($xb) {
      $xb.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
      Write-Output 'X-INVOKED: 废片'
      Start-Sleep -Milliseconds 3500
      $after = FindId $win 'SelectionChip_废片'
      if ($after) { Write-Output ('X-AFTER: chip 仍在 name="' + $after.Current.Name + '"') } else { Write-Output 'X-AFTER: chip 已消失' }
      Shot 'shot-2-after-remove.png'
    } else { Write-Output 'X-SKIP: chip 内未找到按钮' }
  } else { Write-Output 'X-SKIP: 无选中 chip' }

  # 7) 落盘事实核对
  Write-Output 'FILES-AFTER:'
  Get-ChildItem $libDir | ForEach-Object { Write-Output ('  ' + $_.Name) }
  Write-Output 'DONE'
}
finally {
  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 600
  Move-Item $backup $settings -Force
  Write-Output 'SETTINGS-RESTORED'
}
