# repro-hang-observe.ps1 - 首屏卡死纯观测：启动后 90s 零交互，每 2s 采样 Responding+CPU
# 目的：①卡死期 CPU 增量（忙循环 vs 等待型死锁判据）②不注入输入时的自然恢复时间
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W18 {
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[W18]::SetProcessDPIAware() | Out-Null

$exe = Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = $settings + '.bak-obs'
if (Test-Path $backup) { Move-Item $backup $settings -Force }
Copy-Item $settings $backup -Force
# 每次全新路径 = 全冷缩略图缓存（SV_ABLATION 环境变量透传给 viewer 做消融）
$libDir = Join-Path $env:TEMP ('sv-verify-lib-obs-' + (Get-Date -Format 'HHmmss'))

try {
  New-Item -ItemType Directory -Path $libDir | Out-Null
  $bmp0 = New-Object System.Drawing.Bitmap 320, 240
  $g0 = [System.Drawing.Graphics]::FromImage($bmp0)
  $g0.Clear([System.Drawing.Color]::FromArgb(90, 110, 140)); $g0.Dispose()
  $msx = New-Object System.IO.MemoryStream
  $bmp0.Save($msx, [System.Drawing.Imaging.ImageFormat]::Jpeg); $bmp0.Dispose()
  $tpl = $msx.ToArray()
  for ($n = 1; $n -le 1000; $n++) {
    [System.IO.File]::WriteAllBytes((Join-Path $libDir (('o{0:d4}' -f $n) + '[风景].jpg')), $tpl)
  }
  Write-Output ('冷图库已生成: ' + $libDir + '（SV_ABLATION=' + $env:SV_ABLATION + '）')
  $json = Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json
  $json.LastLibraryRoot = $libDir
  $json.shortcuts = @()
  $json | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding UTF8

  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 900
  $launch = Get-Date
  Start-Process $exe
  Start-Sleep -Seconds 3
  $proc = Get-Process viewer -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  [W18]::MoveWindow($proc.MainWindowHandle, 40, 40, 1500, 950, $true) | Out-Null

  # MoveWindow 本身是一次窗口消息——记为 t0，其后 90s 完全零交互
  $t0 = Get-Date
  $prevCpu = $null
  $hangStart = $null
  $hangSamples = 0
  for ($i = 0; $i -lt 45; $i++) {
    Start-Sleep -Seconds 2
    $p = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    if (-not $p) { Write-Output ('t+' + ((Get-Date) - $t0).TotalSeconds.ToString('F0') + 's 进程消失'); break }
    $cpu = $p.CPU
    $delta = if ($null -ne $prevCpu) { [math]::Round($cpu - $prevCpu, 2) } else { 0 }
    $resp = $p.Responding
    $mark = ''
    if (-not $resp) {
      if (-not $hangStart) { $hangStart = ((Get-Date) - $t0).TotalSeconds }
      $hangSamples++
      $mark = ' <<< 无响应'
    } elseif ($hangStart -and $hangSamples -ge 1) {
      Write-Output ('== 恢复于 t+' + ((Get-Date) - $t0).TotalSeconds.ToString('F0') + 's（无响应起于 t+' + $hangStart.ToString('F0') + 's，持续 ~' + ((Get-Date) - $t0).TotalSeconds.ToString('F0') + 's 段内）==')
      $hangStart = $null; $hangSamples = 0
    }
    Write-Output ('t+' + ((Get-Date) - $t0).TotalSeconds.ToString('F0').PadLeft(3) + 's resp=' + $resp + ' cpuΔ=' + $delta + 's/2s' + $mark)
    $prevCpu = $cpu
  }
  Write-Output 'OBSERVE-DONE'
  # 看门狗日志判定（Responding 假活不可信，以应用内心跳为准）
  $log = Join-Path $env:LocalAppData 'SimpleViewer\logs\startup.log'
  $recent = Get-Content $log -Tail 50 | Where-Object { $_ -match 'UI 无响应|UI 恢复响应' }
  Write-Output '=== 看门狗判定 ==='
  if ($recent) { $recent | Select-Object -Last 6 } else { Write-Output '本轮无 [UI 无响应] 记录 = 未卡死' }
}
finally {
  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 600
  Move-Item $backup $settings -Force
  Write-Output 'SETTINGS-RESTORED'
}
