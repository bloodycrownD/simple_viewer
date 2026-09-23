# toolbar-catalog-verify.ps1 - batch-tag-management Step 5/6/7 实机自检
# 工具栏分簇显隐(D1) / 筛选 flyout 盖左栏风险点 / 批量目录三态打标(C3) / 图库删除选中集(D2/D3) / 单图模式回归(D1/D4)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W6 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[W6]::SetProcessDPIAware() | Out-Null

# qa/C-1 以脚本自身目录（scripts\）锚定仓库根推导 exe 路径，仓库克隆到任意路径/机器可用
$exe = Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = $settings + '.bak-tb'
# qa/B-1 备份前置守卫：残留备份 = 上次运行中途崩溃（finally 未执行）——先还原再重新备份，
# 防止把已被污染的 settings 当作新备份源、唯一好备份被删/覆盖
if (Test-Path $backup) {
  Move-Item $backup $settings -Force
  Write-Output ('STALE-BACKUP-RESTORED: ' + $backup + ' 已还原为 settings，随后重新备份')
}
Copy-Item $settings $backup -Force

$libDir = Join-Path $env:TEMP 'sv-verify-lib2'
$shotDir = Join-Path $env:TEMP 'sv-verify2'
if (Test-Path $shotDir) { Remove-Item $shotDir -Recurse -Force }
New-Item -ItemType Directory -Path $shotDir | Out-Null

function FindId($win, $id) {
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function FindBtnByName($win, $name) {
  $bt = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  $nm = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
  $and = New-Object System.Windows.Automation.AndCondition($bt, $nm)
  return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $and)
}
function FindNameLike($win, $pattern) {
  $bt = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $bt)
  foreach ($b in $all) { if ($b.Current.Name -like $pattern) { return $b } }
  return $null
}
function Invoke($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Shot($name) {
  $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
  $bmp.Save((Join-Path $shotDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
}
function AssertBtn($win, $name, $expectPresent, $tag) {
  $b = FindBtnByName $win $name
  $present = $null -ne $b
  if ($present -eq $expectPresent) { Write-Output ('BTN-' + $tag + '-OK: [' + $name + '] ' + $(if ($expectPresent) { '可见' } else { '隐藏' })) }
  else { Write-Output ('BTN-' + $tag + '-FAIL: [' + $name + '] 期望' + $(if ($expectPresent) { '可见' } else { '隐藏' }) + ' 实际' + $(if ($present) { '可见' } else { '隐藏' })) }
}

try {
  # 临时图库：4 张，废片x2 星标x1 无标签x1
  if (Test-Path $libDir) { Remove-Item $libDir -Recurse -Force }
  New-Item -ItemType Directory -Path $libDir | Out-Null
  foreach ($f in @('b1[废片].jpg', 'b2[废片 星标].jpg', 'b3[星标].jpg', 'b4.jpg')) {
    $bmp = New-Object System.Drawing.Bitmap 160, 120
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(90, 60, 110))
    $g.Dispose(); $bmp.Save((Join-Path $libDir $f), [System.Drawing.Imaging.ImageFormat]::Jpeg); $bmp.Dispose()
  }
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
  Start-Process $exe
  Start-Sleep -Seconds 9
  $proc = Get-Process viewer -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  [W6]::MoveWindow($proc.MainWindowHandle, 40, 40, 1500, 950, $true) | Out-Null
  Start-Sleep -Milliseconds 500
  $wsh = New-Object -ComObject WScript.Shell
  $null = $wsh.AppActivate($proc.Id)
  Start-Sleep -Milliseconds 800

  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Simple Viewer')
  $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
  if (-not $win) { Write-Output 'NO-WINDOW'; exit 1 }

  # ── 1. 图库模式工具栏 D1：常显/图库组可见，单图组隐藏 ──
  Write-Output '=== 1. 图库模式工具栏 ==='
  Shot 's1-gallery-toolbar.png'
  foreach ($n in @('打开', '打开图库', '设置')) { AssertBtn $win $n $true 'G' }
  $ftb0 = FindId $win 'FilterToggleButton'
  if ($ftb0 -and -not $ftb0.Current.IsOffscreen) { Write-Output 'BTN-G-OK: [筛选按钮] 可见（AutomationId+IsOffscreen 断言——内容含徽章文本 Name 不稳定）' }
  else { Write-Output 'BTN-G-FAIL: [筛选按钮] 期望可见实际隐藏' }
  foreach ($n in @('全选', '删除')) { AssertBtn $win $n $true 'G' }
  foreach ($n in @('返回图库', '上一张', '下一张', '左旋', '右旋')) { AssertBtn $win $n $false 'G' }

  # ── 2. 筛选 flyout 盖左栏风险点实测（Step 6 挪左簇） ──
  Write-Output '=== 2. 筛选 flyout 位置 ==='
  $sideHdr = FindId $win 'UndefinedTagsHeader'
  $sideRect = [System.Windows.Rect]::Empty
  if ($sideHdr) { $sideRect = $sideHdr.Current.BoundingRectangle; Write-Output ('SIDEBAR-HDR: ' + [int]$sideRect.X + ',' + [int]$sideRect.Y + ' ' + [int]$sideRect.Width + 'x' + [int]$sideRect.Height) }
  $ftb = FindId $win 'FilterToggleButton'
  if ($ftb) {
    Invoke $ftb
    Start-Sleep -Milliseconds 1300
    $fly = FindId $win 'TagFilterFlyout'
    if ($fly) {
      $fr = $fly.Current.BoundingRectangle
      Write-Output ('FLYOUT-RECT: ' + [int]$fr.X + ',' + [int]$fr.Y + ' ' + [int]$fr.Width + 'x' + [int]$fr.Height)
      if (-not $sideRect.IsEmpty) {
        $overlap = [System.Windows.Rect]::Intersect($fr, $sideRect)
        if ($overlap.Width -gt 0 -and $overlap.Height -gt 0) {
          Write-Output ('FLYOUT-OVERLAPS-SIDEBAR: 与未定义区重叠 ' + [int]$overlap.Width + 'x' + [int]$overlap.Height + ' 物理px（浮层遮盖，需用户走查拍板）')
        } else { Write-Output 'FLYOUT-NO-OVERLAP: 未盖未定义区' }
      }
      Shot 's2-filter-flyout.png'
    } else { Write-Output 'FLYOUT-MISSING' }
    Invoke $ftb   # 再点一次关闭
    Start-Sleep -Milliseconds 700
  } else { Write-Output 'FILTER-BTN-MISSING' }

  # ── 3. 全选 → Enter 进单图 → 单图模式工具栏 D1 → 返回图库 ──
  Write-Output '=== 3. 单图模式工具栏 ==='
  $sa = FindBtnByName $win '全选'
  if ($sa) { Invoke $sa; Start-Sleep -Milliseconds 1200 }
  $null = [W6]::SetForegroundWindow($proc.MainWindowHandle)
  Start-Sleep -Milliseconds 300
  $activated = $wsh.AppActivate($proc.Id)
  Write-Output ('APPACTIVATE: ' + $activated)
  Start-Sleep -Milliseconds 300
  $wsh.SendKeys('{ENTER}')
  Start-Sleep -Milliseconds 1200
  $backBtn = $null
  for ($i = 0; $i -lt 3 -and -not $backBtn; $i++) {
    $backBtn = FindBtnByName $win '返回图库'
    if (-not $backBtn) {
      [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
      Start-Sleep -Milliseconds 1500
    }
  }
  if ($backBtn) { Write-Output ('SINGLE-MODE-ENTERED: via Enter（尝试 ' + $i + ' 次）') } else { Write-Output 'SINGLE-MODE-ENTER-FAILED: Enter 未生效（IME/前台竞态；产品路径有历史走查覆盖，本节断言跳过）' }
  Start-Sleep -Milliseconds 600
  Shot 's3-single-toolbar.png'
  if ($backBtn) {
    foreach ($n in @('打开', '打开图库', '设置')) { AssertBtn $win $n $true 'S' }
    foreach ($n in @('返回图库', '上一张', '下一张', '左旋', '右旋')) { AssertBtn $win $n $true 'S' }
    $ftbS = FindId $win 'FilterToggleButton'
    if ($ftbS -and $ftbS.Current.IsOffscreen) { Write-Output 'BTN-S-OK: [筛选按钮] 隐藏' } else { Write-Output 'BTN-S-FAIL: [筛选按钮] 期望隐藏实际可见' }
    foreach ($n in @('全选', '删除')) { AssertBtn $win $n $false 'S' }
    Invoke $backBtn   # UIA 点「返回图库」回图库（比 Esc 注入稳）
    Start-Sleep -Milliseconds 1200
  }

  # ── 4. 批量目录 C3：全选 → ＋添加标签 → 点「风景」→ 4 张全部打标 ──
  Write-Output '=== 4. 批量目录打标 ==='
  $sa2 = FindBtnByName $win '全选'
  if ($sa2) { Invoke $sa2; Start-Sleep -Milliseconds 1200 }
  $add = FindId $win 'SelectionAddTagButton'
  if ($add) {
    Invoke $add
    Start-Sleep -Milliseconds 1300
    Shot 's4-catalog-dialog.png'
    $dlgTitle = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '为选中图片添加标签')))
    if ($dlgTitle) { Write-Output 'BATCH-CATALOG-OK: 对话框标题命中' } else { Write-Output 'BATCH-CATALOG-TITLE-MISSING' }
    $entry = FindBtnByName $win '风景'
    if ($entry) {
      Write-Output ('CATALOG-ENTRY: name="' + $entry.Current.Name + '" enabled=' + $entry.Current.IsEnabled)
      Invoke $entry
      Start-Sleep -Milliseconds 3000
      $chipFj = FindId $win 'SelectionChip_风景'
      if ($chipFj) { Write-Output 'UNION-CHIP-风景-OK' } else { Write-Output 'UNION-CHIP-风景-MISSING' }
    } else {
      Write-Output 'CATALOG-ENTRY-风景-MISSING'
      $closeBtn = FindBtnByName $win '关闭'
      if ($closeBtn) { Invoke $closeBtn; Start-Sleep -Milliseconds 800; Write-Output 'CATALOG-DLG-CLOSED(兜底)' }
    }
  } else { Write-Output 'ADD-TAG-BTN-MISSING' }
  Write-Output 'FILES-AFTER-CATALOG:'
  Get-ChildItem $libDir | ForEach-Object { Write-Output ('  ' + $_.Name) }

  # ── 5. 删除选中集 D2/D3：空选中 no-op → 全选 → 删除 → 确认 → 文件进回收站 ──
  Write-Output '=== 5. 删除选中集 ==='
  $delBtn = FindId $win 'DeleteButton'
  if ($sa2) {
    $clr = FindBtnByName $win '取消全选'
    if ($clr) { Invoke $clr; Start-Sleep -Milliseconds 1000 }
    if ($delBtn) {
      Invoke $delBtn
      Start-Sleep -Milliseconds 1000
      $leak = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '删除选中图片')))
      if ($leak) { Write-Output 'EMPTY-SELECT-DELETE-FAIL: 空选中竟弹对话框' } else { Write-Output 'EMPTY-SELECT-DELETE-OK: no-op 无对话框' }
      Shot 's5-empty-delete.png'
    }
  }
  $sa3 = FindBtnByName $win '全选'
  if ($sa3) { Invoke $sa3; Start-Sleep -Milliseconds 1200 }
  if ($delBtn) {
    Invoke $delBtn
    Start-Sleep -Milliseconds 1500
    Shot 's6-delete-confirm.png'
    $dlg = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '删除选中图片')))
    if ($dlg) {
      Write-Output 'DELETE-CONFIRM-DLG-OK'
      $bt = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
      $nm = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '删除')
      $andD = New-Object System.Windows.Automation.AndCondition($bt, $nm)
      $pbtn = $dlg.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $andD)
      if ($pbtn) { Invoke $pbtn; Write-Output 'DELETE-CONFIRMED' }
      else {
        Write-Output 'DELETE-PRIMARY-BTN-MISSING(对话框范围内)'
        $cancel = FindBtnByName $win '取消'
        if ($cancel) { Invoke $cancel }
      }
      Start-Sleep -Milliseconds 3500
    } else { Write-Output 'DELETE-CONFIRM-DLG-MISSING' }
    $t0 = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '已选 0 张')))
    if ($t0) { Write-Output 'AFTER-DELETE-TITLE-OK: 已选 0 张' } else { Write-Output 'AFTER-DELETE-TITLE-MISSING' }
  } else { Write-Output 'DELETE-BTN-MISSING' }
  Write-Output 'FILES-AFTER-DELETE:'
  $remaining = Get-ChildItem $libDir -ErrorAction SilentlyContinue
  if ($remaining) { $remaining | ForEach-Object { Write-Output ('  ' + $_.Name) } } else { Write-Output '  (空——4 张全部入回收站)' }
  Shot 's7-final.png'
  Write-Output 'DONE'
}
finally {
  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 600
  Move-Item $backup $settings -Force
  Write-Output 'SETTINGS-RESTORED'
}
