# repro-hang-1000.ps1 - 「多次给 30 张图片打标/移除卡死」压力复现（用户规格：1000 张图库）
# 布局：1000 张（30 张带「测试」标签 + 970 张带「风景」）→ 筛选「测试」命中恰好 30 张
# 每轮：全选(30) → 右栏＋批量打「星标」→ 全选 → 右栏✕移除「星标」，共 4 轮（8 次批量改名）
# 监控：2s 采样 Responding+CPU；连续 3 次 False(≥6s)=卡死 → 等 25s 让应用看门狗自动落 hang dump → 收集证据
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W17 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[W17]::SetProcessDPIAware() | Out-Null

$exe = Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$applog = Join-Path $env:LocalAppData 'SimpleViewer\logs\startup.log'
$hangDir = Join-Path $env:LocalAppData 'SimpleViewer\logs\hangdumps'
$backup = $settings + '.bak-h1000'
if (Test-Path $backup) {
  Move-Item $backup $settings -Force
  Write-Output ('STALE-BACKUP-RESTORED: ' + $backup)
}
Copy-Item $settings $backup -Force
$libDir = Join-Path $env:TEMP ('sv-verify-lib-h1000-' + (Get-Date -Format 'HHmmss'))  # 每次全新路径=全冷缓存
$logMark = "[repro-hang-1000 $(Get-Date -Format HH:mm:ss)]"

function FindBtnByName($win, $name) {
  $bt = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  $nm = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
  $and = New-Object System.Windows.Automation.AndCondition($bt, $nm)
  return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $and)
}
function InvokeEl($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function GetWin {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Simple Viewer')
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
}
function CheckHang($proc, $label) {
  # 连续采样：3 次 Responding=False（6s+）判定卡死
  $bad = 0
  for ($i = 0; $i -lt 10; $i++) {
    Start-Sleep -Seconds 2
    $p = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    if (-not $p) { Write-Output ('[' + $label + '] 进程消失（崩溃）'); return 'crash' }
    $cpu = $p.CPU
    if (-not $p.Responding) {
      $bad++
      $delta = if ($null -ne $script:prevCpu) { [math]::Round(($cpu - $script:prevCpu), 2) } else { -1 }
      Write-Output ('[' + $label + '] 无响应 x' + $bad + ' CPU增量=' + $delta + 's/2s')
      if ($bad -ge 3) {
        Write-Output ('[' + $label + '] 卡死实锤——外部抓现场（dotnet-stack/dotnet-dump 走诊断管道，EDR 不拦）')
        $stackFile = Join-Path $env:TEMP ('sv-hang-stack-' + (Get-Date -Format HHmmss) + '.txt')
        try { dotnet-stack report -p $proc.Id > $stackFile 2>&1; Write-Output ('STACK-SAVED: ' + $stackFile) } catch { Write-Output ('dotnet-stack 失败: ' + $_.Exception.Message) }
        $dumpFile = Join-Path $env:TEMP ('sv-hang-' + (Get-Date -Format HHmmss) + '.dmp')
        try { dotnet-dump collect -p $proc.Id -o $dumpFile 2>&1 | Select-Object -Last 1 } catch { Write-Output ('dotnet-dump 失败: ' + $_.Exception.Message) }
        return 'hang'
      }
    }
    else { $bad = 0 }
    $script:prevCpu = $cpu
  }
  return 'ok'
}

try {
  if (Test-Path $libDir) { Remove-Item $libDir -Recurse -Force }
  New-Item -ItemType Directory -Path $libDir | Out-Null
  # 模板字节 ×1000（秒级生成；缩略图按路径缓存，内容相同不影响每张独立解码）
  $bmp = New-Object System.Drawing.Bitmap 320, 240
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::FromArgb(90, 110, 140)); $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Jpeg); $bmp.Dispose()
  $template = $ms.ToArray()
  for ($n = 1; $n -le 1000; $n++) {
    $tag = if ($n -le 30) { '[测试]' } else { '[风景]' }
    [System.IO.File]::WriteAllBytes((Join-Path $libDir (('t{0:d4}' -f $n) + $tag + '.jpg')), $template)
  }
  Write-Output '图库已生成：1000 张（30 张「测试」+ 970 张「风景」）'

  $json = Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json
  $json.LastLibraryRoot = $libDir
  $json.shortcuts = @()
  $json.tagGroups = @(
    [PSCustomObject]@{ id = 'vg1'; name = '测试组'; exclusive = $false; tags = @(
      [PSCustomObject]@{ id = 'vt9'; name = '测试' },
      [PSCustomObject]@{ id = 'vt8'; name = '星标' }) },
    [PSCustomObject]@{ id = 'vg2'; name = '主题'; exclusive = $false; tags = @([PSCustomObject]@{ id = 'vt1'; name = '风景' }) }
  )
  $json | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding UTF8

  $logStart = (Get-Item $applog -ErrorAction SilentlyContinue).Length
  if (-not $logStart) { $logStart = 0 }

  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 900
  Start-Process $exe
  Write-Output '等待启动+扫描+首屏缩略图（带卡死监测——卡死高发段即此 0.6~23s）…'
  Start-Sleep -Seconds 4
  $proc = Get-Process viewer -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  [W17]::MoveWindow($proc.MainWindowHandle, 40, 40, 1500, 950, $true) | Out-Null
  Start-Sleep -Milliseconds 800
  # 启动首屏段监控（替代原硬等 24s）：CheckHang 采样 ~20s 覆盖卡死窗口，卡死自动外部抓栈
  $st = CheckHang $proc '启动首屏'
  Write-Output ('启动首屏状态: ' + $st)
  if ($st -ne 'ok') { throw "HANG-OR-CRASH at startup: $st" }
  $win = GetWin
  if (-not $win) { Write-Output 'NO-WINDOW'; exit 1 }

  # 筛选「测试」→ 命中恰好 30 张
  $t = FindBtnByName $win '测试'
  if (-not $t) { Write-Output 'TAGROW-测试-MISSING'; exit 1 }
  InvokeEl $t
  Start-Sleep -Milliseconds 2500
  Write-Output '筛选「测试」已激活（命中 30 张）'
  $st = CheckHang $proc '筛选后'
  if ($st -ne 'ok') { throw "HANG-OR-CRASH: $st" }

  foreach ($round in 1..4) {
    # ── 打标轮：全选 → ＋ → 星标 ──
    $sa = FindBtnByName $win '全选'
    if (-not $sa) { Write-Output ('[r' + $round + '] 全选按钮缺失'); break }
    InvokeEl $sa; Start-Sleep -Milliseconds 2000
    $add = FindBtnByName $win '＋'
    if ($add) {
      InvokeEl $add; Start-Sleep -Milliseconds 1500
      $entry = FindBtnByName $win '星标'
      if ($entry) {
        $t0 = Get-Date
        InvokeEl $entry
        Write-Output ('[r' + $round + '] 批量打标「星标」x30 已发起')
        $st = CheckHang $proc ('r' + $round + '-打标')
        Write-Output ('[r' + $round + '] 打标轮状态: ' + $st + ' 耗时 ' + ((Get-Date) - $t0).TotalSeconds.ToString('F0') + 's')
        if ($st -ne 'ok') { throw "HANG-OR-CRASH at r$round add: $st" }
      } else { Write-Output ('[r' + $round + '] 目录星标缺失') }
    }

    # ── 移除轮：全选 → 右栏 chip ✕ 移除「星标」──
    $sa = FindBtnByName $win '全选'
    if ($sa) { InvokeEl $sa; Start-Sleep -Milliseconds 2000 }
    $xbtn = $null
    $btCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btCond)
    foreach ($b in $all) {
      if ($b.Current.Name -eq '移除标签 星标') { $xbtn = $b; break }
    }
    if ($xbtn) {
      $t0 = Get-Date
      InvokeEl $xbtn
      Write-Output ('[r' + $round + '] 批量移除「星标」x30 已发起')
      $st = CheckHang $proc ('r' + $round + '-移除')
      Write-Output ('[r' + $round + '] 移除轮状态: ' + $st + ' 耗时 ' + ((Get-Date) - $t0).TotalSeconds.ToString('F0') + 's')
      if ($st -ne 'ok') { throw "HANG-OR-CRASH at r$round remove: $st" }
    } else { Write-Output ('[r' + $round + '] 右栏无「移除标签 星标」chip（可能已移尽/需重选）') }
  }

  # ── 大集合风暴轮（贴近用户真实场景）：筛选「风景」（970 张命中）→ 全选 → 打标 → 移除 ──
  Write-Output '=== 大集合轮：筛选「风景」（~970 张命中）==='
  $fj2 = FindBtnByName $win '风景'
  if ($fj2) {
    InvokeEl $fj2
    Write-Output '筛选「风景」已发起——等待大集合重排+缩略图风暴（本段是用户卡死主嫌疑区）'
    $st = CheckHang $proc '大集合-筛选'
    Write-Output ('大集合筛选状态: ' + $st)
    if ($st -ne 'ok') { throw "HANG-OR-CRASH at big-filter: $st" }

    $sa = FindBtnByName $win '全选'
    if ($sa) {
      InvokeEl $sa
      Start-Sleep -Milliseconds 3000
      $st = CheckHang $proc '大集合-全选'
      if ($st -ne 'ok') { throw "HANG-OR-CRASH at big-select: $st" }
      $add = FindBtnByName $win '＋'
      if ($add) {
        InvokeEl $add; Start-Sleep -Milliseconds 1500
        $entry = FindBtnByName $win '星标'
        if ($entry) {
          InvokeEl $entry
          Write-Output '[大集合] 批量打标「星标」x970 已发起'
          $st = CheckHang $proc '大集合-打标'
          Write-Output ('大集合打标状态: ' + $st)
          if ($st -ne 'ok') { throw "HANG-OR-CRASH at big-add: $st" }
        }
      }
    }
  } else { Write-Output 'TAGROW-风景-MISSING（跳过大集合轮）' }

  Write-Output '=== 全程无卡死——输出管线耗时画像（DiagnosticTrace 尾部）==='
}
catch {
  Write-Output ('异常/卡死分支: ' + $_.Exception.Message)
  Write-Output '等 25s 让应用看门狗自动落 hang dump…'
  Start-Sleep -Seconds 25
}
finally {
  # 收集证据：hang dump 与新增日志
  if (Test-Path $hangDir) {
    Get-ChildItem $hangDir -Filter '*.dmp' | Sort-Object LastWriteTime -Descending | Select-Object -First 3 | ForEach-Object {
      Write-Output ('HANG-DUMP: ' + $_.FullName + ' ' + [math]::Round($_.Length / 1MB, 1) + 'MB ' + $_.LastWriteTime)
    }
  } else { Write-Output 'HANG-DUMP: 无目录（未触发或旧版本）' }
  if (Test-Path $applog) {
    $fs = [System.IO.File]::OpenRead($applog)
    $fs.Seek($logStart, 'Begin') | Out-Null
    $sr = New-Object System.IO.StreamReader($fs)
    $tail = $sr.ReadToEnd(); $sr.Dispose(); $fs.Dispose()
    $marks = ($tail -split "`r?`n") | Where-Object { $_ -match 'tag-op:|filter:|sync-renamed:|thumb:apply|UI 无响应|UI 恢复|卡死现场' } | Select-Object -Last 60
    Write-Output '=== 本次运行管线追踪（尾部 60 条）==='
    $marks
  }
  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 600
  Move-Item $backup $settings -Force
  Write-Output 'SETTINGS-RESTORED'
}
