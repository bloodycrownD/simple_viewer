# repro-crash-tag.ps1 - 「打标/移除标签时崩溃闪退」压力复现
# 手法：大图库（40 张 1200x1800 PNG，逼近用户 245 张大图场景）+ 筛选 + 循环 进单图→✕移除标签→＋添加标签→返回
# 监测 viewer 进程存活，崩溃（进程消失/闪退）即停并报告轮次
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W16 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
"@
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[W16]::SetProcessDPIAware() | Out-Null

$exe = Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = $settings + '.bak-cr'
if (Test-Path $backup) {
  Move-Item $backup $settings -Force
  Write-Output ('STALE-BACKUP-RESTORED: ' + $backup)
}
Copy-Item $settings $backup -Force
$libDir = Join-Path $env:TEMP 'sv-verify-lib-cr'

function FindBtnByName($win, $name) {
  $bt = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  $nm = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
  $and = New-Object System.Windows.Automation.AndCondition($bt, $nm)
  return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $and)
}
function InvokeEl($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Alive { [bool](Get-Process viewer -ErrorAction SilentlyContinue) }

try {
  if (Test-Path $libDir) { Remove-Item $libDir -Recurse -Force }
  New-Item -ItemType Directory -Path $libDir | Out-Null
  # 40 张 1200x1800 大 PNG，2/3 带「风景」
  for ($n = 1; $n -le 40; $n++) {
    $tag = if ($n % 3 -ne 0) { '[风景]' } else { '' }
    $bmp = New-Object System.Drawing.Bitmap 1200, 1800
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb((20 + $n * 5) % 220, 80, 110))
    $g.Dispose()
    $bmp.Save((Join-Path $libDir ('p' + $n + $tag + '.png')), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
  }
  Write-Output '图库已生成（40 张大 PNG）'
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
  Start-Sleep -Seconds 14
  $proc = Get-Process viewer -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  [W16]::MoveWindow($proc.MainWindowHandle, 40, 40, 1500, 950, $true) | Out-Null
  Start-Sleep -Milliseconds 800
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Simple Viewer')
  $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
  if (-not $win) { Write-Output 'NO-WINDOW'; exit 1 }

  $fj = FindBtnByName $win '风景'
  if ($fj) { InvokeEl $fj; Start-Sleep -Milliseconds 2500; Write-Output '筛选已激活' }

  $crashed = $false
  foreach ($round in 1..20) {
    $idx = (($round - 1) % 13) + 1
    $cardName = 'p' + $idx + '.png'
    $card = FindBtnByName $win $cardName
    $r0 = [System.Windows.Rect]::Empty
    if ($card) {
      $rr = $card.Current.BoundingRectangle
      if (-not [double]::IsNaN($rr.Left) -and -not [double]::IsNaN($rr.Top) -and $rr.Width -gt 50) { $r0 = $rr }
    }
    if ($r0.IsEmpty) {
      $btCond2 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
      $allBtns2 = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btCond2)
      foreach ($b in $allBtns2) {
        if ($b.Current.Name -match '^p\d+\.png$') {
          $rr = $b.Current.BoundingRectangle
          if (-not [double]::IsNaN($rr.Left) -and -not [double]::IsNaN($rr.Top) -and $rr.Top -gt 150 -and $rr.Bottom -lt 900 -and $rr.Width -gt 50) { $card = $b; $r0 = $rr; break }
        }
      }
    }
    if ($r0.IsEmpty) { Write-Output ('[r' + $round + '] 无可见卡片'); continue }
    [W16]::SetCursorPos([int](($r0.Left + $r0.Right) / 2), [int](($r0.Top + $r0.Bottom) / 2)) | Out-Null
    Start-Sleep -Milliseconds 80
    [W16]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [W16]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 500
    [W16]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
    [W16]::keybd_event(0x0D, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60; [W16]::keybd_event(0x0D, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 2200
    if (-not (Alive)) { Write-Output ('[r' + $round + '] 崩溃于进单图后'); $crashed = $true; break }
    $back = FindBtnByName $win '◀ 返回图库'
    if (-not $back) { Write-Output ('[r' + $round + '] 未进入单图'); continue }

    if ($round % 2 -eq 1) {
      $btCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
      $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btCond)
      $xbtn = $null
      foreach ($b in $all) {
        $r = $b.Current.BoundingRectangle
        if ($b.Current.Name -eq '✕' -and $r.X -gt 1100 -and $r.Y -gt 250 -and $r.Y -lt 700) { $xbtn = $b; break }
      }
      if ($xbtn) { InvokeEl $xbtn; Start-Sleep -Milliseconds 2500; Write-Output ('[r' + $round + '] 已✕移除标签') }
    } else {
      $add = FindBtnByName $win '＋'
      if ($add) {
        InvokeEl $add
        Start-Sleep -Milliseconds 1500
        $entry = FindBtnByName $win '星标'
        if ($entry) { InvokeEl $entry; Start-Sleep -Milliseconds 2500; Write-Output ('[r' + $round + '] 已＋添加星标') }
      }
    }
    if (-not (Alive)) { Write-Output ('[r' + $round + '] 崩溃于标签处理后'); $crashed = $true; break }

    $back = FindBtnByName $win '◀ 返回图库'
    if ($back -and $back.Current.IsEnabled) { InvokeEl $back; Start-Sleep -Milliseconds 2000 }
    else { Write-Output ('[r' + $round + '] 返回按钮异常'); break }
    if (-not (Alive)) { Write-Output ('[r' + $round + '] 崩溃于返回后'); $crashed = $true; break }
  }
  if (-not $crashed) { Write-Output '全程未崩溃（本轮未复现）' }
  Write-Output 'DONE'
}
finally {
  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 600
  Move-Item $backup $settings -Force
  Write-Output 'SETTINGS-RESTORED'
}
