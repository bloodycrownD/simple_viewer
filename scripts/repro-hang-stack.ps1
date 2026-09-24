# repro-hang-stack.ps1 - 冷缓存首屏卡死定点抓栈：全新图库路径（全冷）→ t+4/9/16s 三次 dotnet-stack
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W19 {
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[W19]::SetProcessDPIAware() | Out-Null

$exe = Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = $settings + '.bak-stk'
if (Test-Path $backup) { Move-Item $backup $settings -Force }
Copy-Item $settings $backup -Force
$libDir = Join-Path $env:TEMP ('sv-verify-lib-cold-' + (Get-Date -Format 'HHmmss'))
$outDir = Join-Path $env:TEMP 'sv-hang-stacks'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

try {
  # 全新路径图库 = 全冷缓存（复用字节模板快速生成 1000 张）
  New-Item -ItemType Directory -Path $libDir | Out-Null
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
  Write-Output ('全冷图库: ' + $libDir)
  $json = Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json
  $json.LastLibraryRoot = $libDir
  $json.shortcuts = @()
  $json.tagGroups = @(
    [PSCustomObject]@{ id = 'vg1'; name = '主题'; exclusive = $false; tags = @([PSCustomObject]@{ id = 'vt1'; name = '风景' }, [PSCustomObject]@{ id = 'vt2'; name = '星标' }) }
  )
  $json | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding UTF8

  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 900
  $t0 = Get-Date
  Start-Process $exe
  Start-Sleep -Seconds 3
  $proc = Get-Process viewer -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  [W19]::MoveWindow($proc.MainWindowHandle, 40, 40, 1500, 950, $true) | Out-Null

  # 卡死窗口（首屏后 0.9~23s）内定点抓三次托管栈
  foreach ($wait in @(1, 6, 13)) {
    Start-Sleep -Seconds $wait
    $f = Join-Path $outDir ('stack-t+' + (((Get-Date) - $t0).TotalSeconds).ToString('F0') + 's.txt')
    dotnet-stack report -p $proc.Id > $f 2>&1
    Write-Output ('抓栈: ' + $f + ' (' + [math]::Round((Get-Item $f).Length / 1KB) + 'KB)')
  }
  Write-Output 'STACKS-DONE（应用继续跑完剩余窗口后收尾）'
  Start-Sleep -Seconds 10
}
finally {
  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 600
  Move-Item $backup $settings -Force
  Write-Output 'SETTINGS-RESTORED'
  # 输出每个栈文件中 UI 线程（含 SimpleViewer 帧最多）的关键段
  Get-ChildItem $outDir -Filter 'stack-t+*.txt' | Sort-Object LastWriteTime -Descending | Select-Object -First 3 | ForEach-Object {
    Write-Output ('===== ' + $_.Name + ' =====')
    $lines = Get-Content $_.FullName
    # 找含 SimpleViewer 帧的线程块，输出每块头 12 行
    $blocks = $lines -join "`n" -split "Thread \("
    foreach ($b in $blocks) {
      if ($b -match 'SimpleViewer') {
        ($b -split "`n" | Select-Object -First 14) -join "`n"
        Write-Output '-----'
      }
    }
  }
}
