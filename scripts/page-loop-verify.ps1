# page-loop-verify.ps1 - stale-bitmap repro under rapid back-and-forth paging
# (2026-09-24 RO_E_CLOSED: retired SoftwareBitmapSource.Dispose closes the bitmap it
# presented; LRU hit on such an entry blew up the swap-source path; now cache-hit
# health-check + one retry re-decodes). Scenario: -d direct open single view,
# UIA next/prev alternating 12-forward/12-back x3 cycles (LRU capacity 6 vs
# retirement keep-window 8 = hard churn), then assert no new load-failure entries
# in startup.log and final frame shows real image content.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WPL {
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
"@
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[WPL]::SetProcessDPIAware() | Out-Null

# button names / log marker via char codes (script stays pure ASCII)
$nextName = [string][char]0x4E0B + [string][char]0x4E00 + [string][char]0x5F20      # xia yi zhang
$prevName = [string][char]0x4E0A + [string][char]0x4E00 + [string][char]0x5F20      # shang yi zhang
$failMark = [string][char]0x5355 + [string][char]0x56FE + [string][char]0x52A0 + [string][char]0x8F7D + [string][char]0x5931 + [string][char]0x8D25   # single-image load failure

$exe = Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = $settings + '.bak-pl'
$log = Join-Path $env:LocalAppData 'SimpleViewer\logs\startup.log'
if (Test-Path $backup) { Move-Item $backup $settings -Force }
Copy-Item $settings $backup -Force

function FindBtn($win, $name) {
  $bt = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  $nm = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
  return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition($bt, $nm)))
}
function HasImageContent($hwnd) {
  $bmp = New-Object System.Drawing.Bitmap 1600, 900
  $g2 = [System.Drawing.Graphics]::FromImage($bmp)
  $hd = $g2.GetHdc()
  [WPL]::PrintWindow($hwnd, $hd, 2) | Out-Null
  $g2.ReleaseHdc($hd); $g2.Dispose()
  $nonChrome = 0; $samples = 0
  for ($y = 260; $y -lt 860; $y += 30) {
    for ($x = 300; $x -lt 1500; $x += 30) {
      $c = $bmp.GetPixel($x, $y); $samples++
      if (-not (($c.R -ge 28 -and $c.R -le 50) -and ($c.G -ge 28 -and $c.G -le 52) -and ($c.B -ge 36 -and $c.B -le 58))) { $nonChrome++ }
    }
  }
  $bmp.Dispose()
  return ($nonChrome -ge [int]($samples * 0.15))
}

try {
  $libDir = 'F:\Pictures\Storage\new\114299\good'
  $json = Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json
  $json.LastLibraryRoot = $libDir
  $json.shortcuts = @()
  $json | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding UTF8

  $launchAt = Get-Date
  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 800
  Start-Process $exe -ArgumentList @('-d', $libDir, '-i', '8')
  Start-Sleep -Seconds 8
  $proc = Get-Process viewer -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  [WPL]::MoveWindow($proc.MainWindowHandle, 40, 40, 1600, 900, $true) | Out-Null
  Start-Sleep -Milliseconds 1200

  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Simple Viewer')
  $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
  if (-not $win) { Write-Output 'NO-WINDOW'; exit 1 }

  $invokes = 0
  $missed = 0
  for ($cycle = 1; $cycle -le 3; $cycle++) {
    for ($i = 0; $i -lt 12; $i++) {
      $b = FindBtn $win $nextName
      if ($b) { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); $invokes++ } else { $missed++ }
      Start-Sleep -Milliseconds 700
    }
    for ($i = 0; $i -lt 12; $i++) {
      $b = FindBtn $win $prevName
      if ($b) { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); $invokes++ } else { $missed++ }
      Start-Sleep -Milliseconds 700
    }
    Write-Output ("cycle ${cycle}: invokes=" + $invokes + " missed=" + $missed)
  }

  Start-Sleep -Seconds 3
  Write-Output ("FINAL-FRAME-HAS-IMAGE: " + (HasImageContent $proc.MainWindowHandle))

  $failEntries = Get-Content $log -Encoding UTF8 |
    Where-Object { $_.Contains($failMark) -and $_.StartsWith('[') } |
    Where-Object { [datetime]::ParseExact($_.Substring(1,19), 'yyyy-MM-dd HH:mm:ss', $null) -ge $launchAt }
  if ($failEntries) { Write-Output 'LOAD-FAILURES-DETECTED'; $failEntries | ForEach-Object { Write-Output $_ } }
  else { Write-Output 'NO-LOAD-FAILURE (fix holds)' }
  Write-Output 'DONE'
}
finally {
  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 500
  Move-Item $backup $settings -Force
  Write-Output 'SETTINGS-RESTORED'
}
