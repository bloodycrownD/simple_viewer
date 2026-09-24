# repro-hang-dump.ps1 - 冷缓存卡死中段 dotnet-dump collect（诊断管道）→ 离线 SOS dumpstack 看 native 帧
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W21 {
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[W21]::SetProcessDPIAware() | Out-Null

$exe = Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = $settings + '.bak-dmp'
if (Test-Path $backup) { Move-Item $backup $settings -Force }
Copy-Item $settings $backup -Force
$libDir = Join-Path $env:TEMP ('sv-verify-lib-dmp-' + (Get-Date -Format 'HHmmss'))
$dumpFile = Join-Path $env:TEMP ('sv-hang-live-' + (Get-Date -Format 'HHmmss') + '.dmp')

try {
  New-Item -ItemType Directory -Path $libDir | Out-Null
  $bmp = New-Object System.Drawing.Bitmap 320, 240
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::FromArgb(90, 110, 140)); $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Jpeg); $bmp.Dispose()
  $template = $ms.ToArray()
  for ($n = 1; $n -le 1000; $n++) {
    [System.IO.File]::WriteAllBytes((Join-Path $libDir (('m{0:d4}' -f $n) + '[风景].jpg')), $template)
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
  [W21]::MoveWindow($proc.MainWindowHandle, 40, 40, 1500, 950, $true) | Out-Null

  Write-Output '等 8s 进入卡死中段…'
  Start-Sleep -Seconds 8
  Write-Output ('抓 dump（诊断管道）: ' + $dumpFile)
  dotnet-dump collect -p $proc.Id -o $dumpFile 2>&1 | Select-Object -Last 1
  Write-Output ('dump 大小: ' + [math]::Round((Get-Item $dumpFile).Length / 1MB, 1) + 'MB')
  Start-Sleep -Seconds 4
}
finally {
  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 600
  Move-Item $backup $settings -Force
  Write-Output 'SETTINGS-RESTORED'
  Write-Output ('DUMP-AT: ' + $dumpFile)
}
