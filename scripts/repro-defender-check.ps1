# repro-defender-check.ps1 - 冷图库启动卡死期间采样 MsMpEng（Defender）CPU 与 IO
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W20 {
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[W20]::SetProcessDPIAware() | Out-Null

$exe = Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = $settings + '.bak-dfv'
if (Test-Path $backup) { Move-Item $backup $settings -Force }
Copy-Item $settings $backup -Force
$libDir = Join-Path $env:TEMP ('sv-verify-lib-dfv-' + (Get-Date -Format 'HHmmss'))

try {
  New-Item -ItemType Directory -Path $libDir | Out-Null
  $bmp = New-Object System.Drawing.Bitmap 320, 240
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::FromArgb(90, 110, 140)); $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Jpeg); $bmp.Dispose()
  $template = $ms.ToArray()
  for ($n = 1; $n -le 1000; $n++) {
    [System.IO.File]::WriteAllBytes((Join-Path $libDir (('d{0:d4}' -f $n) + '[风景].jpg')), $template)
  }
  Write-Output ('冷图库: ' + $libDir)
  $json = Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json
  $json.LastLibraryRoot = $libDir
  $json.shortcuts = @()
  $json | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding UTF8

  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 900
  Start-Process $exe
  Start-Sleep -Seconds 3
  $proc = Get-Process viewer -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  [W20]::MoveWindow($proc.MainWindowHandle, 40, 40, 1500, 950, $true) | Out-Null

  $t0 = Get-Date
  $prevV = $null; $prevM = $null
  for ($i = 0; $i -lt 16; $i++) {
    Start-Sleep -Seconds 2
    $v = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    $m = Get-Process MsMpEng -ErrorAction SilentlyContinue
    $dv = if ($v -and $null -ne $prevV) { [math]::Round($v.CPU - $prevV, 2) } else { 0 }
    $dm = if ($m -and $null -ne $prevM) { [math]::Round(($m | Measure-Object CPU -Sum).Sum - $prevM, 2) } else { 0 }
    Write-Output ('t+' + ((Get-Date) - $t0).TotalSeconds.ToString('F0').PadLeft(3) + 's viewerCPU=' + $dv + ' MsMpEngCPU=' + $dm + ' resp=' + $(if ($v) { $v.Responding } else { 'GONE' }))
    if ($v) { $prevV = $v.CPU }
    if ($m) { $prevM = ($m | Measure-Object CPU -Sum).Sum }
  }
}
finally {
  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 600
  Move-Item $backup $settings -Force
  Write-Output 'SETTINGS-RESTORED'
}
