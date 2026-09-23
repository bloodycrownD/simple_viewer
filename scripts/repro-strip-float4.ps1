# repro-strip-float4.ps1 - 「筛选+多次进单图处理标签后返回悬空」复现
# 每轮：进单图(单击+raw Enter) → 处理标签(移除当前图标签 → 触发改名+筛选重应用+回执) → 记录返回按钮几何/状态 → 返回图库
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W13 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
"@
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[W13]::SetProcessDPIAware() | Out-Null

$exe = Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = $settings + '.bak-sf4'
if (Test-Path $backup) {
  Move-Item $backup $settings -Force
  Write-Output ('STALE-BACKUP-RESTORED: ' + $backup)
}
Copy-Item $settings $backup -Force
$libDir = Join-Path $env:TEMP 'sv-verify-lib-sf4'
$shotDir = Join-Path $env:TEMP 'sv-verify-sf4'
if (Test-Path $shotDir) { Remove-Item $shotDir -Recurse -Force }
New-Item -ItemType Directory -Path $shotDir | Out-Null

function FindBtnByName($win, $name) {
  $bt = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  $nm = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
  $and = New-Object System.Windows.Automation.AndCondition($bt, $nm)
  return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $and)
}
function InvokeEl($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function BackState($win, $tag) {
  $b = FindBtnByName $win '◀ 返回图库'
  if (-not $b) { Write-Output ($tag + ' back=MISSING'); return }
  $r = $b.Current.BoundingRectangle
  $en = $b.Current.IsEnabled
  Write-Output ($tag + ' back T=' + [int]$r.Top + ' L=' + [int]$r.Left + ' enabled=' + $en)
}
function ShotWin($proc, $name) {
  Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W14 {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
}
"@
  $bmp = New-Object System.Drawing.Bitmap 1500, 950
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $hdc = $g.GetHdc()
  [W14]::PrintWindow($proc.MainWindowHandle, $hdc, 2) | Out-Null
  $g.ReleaseHdc($hdc)
  $bmp.Save((Join-Path $shotDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose(); $g.Dispose()
}

try {
  if (Test-Path $libDir) { Remove-Item $libDir -Recurse -Force }
  New-Item -ItemType Directory -Path $libDir | Out-Null
  # 5 张：4 张带「风景」（筛选命中），1 张无标签；竖图比例（3:4）不铺满画布放大空白观感
  $names = @('a1[风景].jpg', 'a2[风景].jpg', 'a3[风景].jpg', 'a4[风景].jpg', 'a5.jpg')
  $i = 1
  foreach ($f in $names) {
    $bmp = New-Object System.Drawing.Bitmap 450, 600
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb((50 + $i * 20), 80, 110))
    $g.Dispose(); $bmp.Save((Join-Path $libDir $f), [System.Drawing.Imaging.ImageFormat]::Jpeg); $bmp.Dispose()
    $i++
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
  [W13]::MoveWindow($proc.MainWindowHandle, 40, 40, 1500, 950, $true) | Out-Null
  Start-Sleep -Milliseconds 800
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Simple Viewer')
  $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
  if (-not $win) { Write-Output 'NO-WINDOW'; exit 1 }

  # 激活筛选「风景」
  $fj = FindBtnByName $win '风景'
  if ($fj) { InvokeEl $fj; Start-Sleep -Milliseconds 1200; Write-Output '筛选已激活' }

  # 循环 4 轮：进单图 → 处理标签（交替 移除/添加 星标）→ 记录状态 → 返回图库
  foreach ($round in 1..4) {
    $cardName = 'a' + $round + '.jpg'
    $card = FindBtnByName $win $cardName
    if (-not $card) { Write-Output ('[r' + $round + '] CARD ' + $cardName + ' MISSING（可能已被移出筛选）'); continue }
    # 单击选中 + raw Enter 进单图
    $r0 = $card.Current.BoundingRectangle
    [W13]::SetCursorPos([int](($r0.Left + $r0.Right) / 2), [int](($r0.Top + $r0.Bottom) / 2)) | Out-Null
    Start-Sleep -Milliseconds 80
    [W13]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [W13]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 500
    [W13]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 150
    [W13]::keybd_event(0x0D, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60; [W13]::keybd_event(0x0D, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 1800
    $back = FindBtnByName $win '◀ 返回图库'
    if (-not $back) { Write-Output ('[r' + $round + '] 未进入单图（Enter 注入失败），跳过'); continue }
    BackState $win ('[r' + $round + '] 进单图后')

    # 处理标签：奇数轮移除当前图某标签（图可能出筛选）、偶数轮添加「星标」
    if ($round % 2 -eq 1) {
      # 单图右栏 chip ✕ 为裸「✕」Name——按右栏中部位置过滤
      $btCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
      $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btCond)
      $xbtn = $null
      foreach ($b in $all) {
        $r = $b.Current.BoundingRectangle
        if ($b.Current.Name -eq '✕' -and $r.X -gt 1100 -and $r.Y -gt 250 -and $r.Y -lt 600) { $xbtn = $b; break }
      }
      if ($xbtn) {
        InvokeEl $xbtn
        Start-Sleep -Milliseconds 2200
        Write-Output ('[r' + $round + '] 已点 ✕（移除当前图某标签）')
      } else {
        Write-Output ('[r' + $round + '] 未找到右栏 ✕')
      }
    } else {
      $add = FindBtnByName $win '＋'
      if (-not $add) { $add = FindBtnByName $win '添加标签' }
      if ($add) {
        InvokeEl $add
        Start-Sleep -Milliseconds 1200
        $entry = FindBtnByName $win '星标'
        if ($entry) { InvokeEl $entry; Start-Sleep -Milliseconds 2000; Write-Output ('[r' + $round + '] 已添加标签 星标') }
        else { Write-Output ('[r' + $round + '] 目录无星标条目'); ShotWin $proc ('r' + $round + '-catalog.png') }
      } else { Write-Output ('[r' + $round + '] 找不到添加标签入口') }
    }
    BackState $win ('[r' + $round + '] 处理标签后')
    ShotWin $proc ('r' + $round + '-after.png')

    # 返回图库
    $back = FindBtnByName $win '◀ 返回图库'
    if ($back -and $back.Current.IsEnabled) {
      InvokeEl $back
      Start-Sleep -Milliseconds 1500
      Write-Output ('[r' + $round + '] 已返回图库')
    } else {
      Write-Output ('[r' + $round + '] 返回按钮缺失或禁用——悬空实锤？')
      ShotWin $proc ('r' + $round + '-STUCK.png')
      break
    }
  }
  Write-Output 'DONE'
}
finally {
  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 600
  Move-Item $backup $settings -Force
  Write-Output 'SETTINGS-RESTORED'
}
