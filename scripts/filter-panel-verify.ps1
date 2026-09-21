# end-to-end verify: library loads -> panel opens -> add empty condition -> no wall reset
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W3 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[W3]::SetProcessDPIAware() | Out-Null

$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = $settings + '.bak'
if (Test-Path $backup) { Remove-Item $backup -Force }
Copy-Item $settings $backup -Force

try {
  $json = Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json
  $json.LastLibraryRoot = 'D:\Dev\Python\simple_viewer\test-library'
  $json | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding UTF8

  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 900
  Start-Process 'D:\Dev\Python\simple_viewer\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
  Start-Sleep -Seconds 9
  $proc = Get-Process viewer -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  [W3]::ShowWindow($proc.MainWindowHandle, 9) | Out-Null
  # AppActivate carries foreground permission (raw SetForegroundWindow is denied by the
  # foreground lock from a background console, and a Flyout on a non-foreground window
  # light-dismisses immediately -- the previous run's panel failed to stay open).
  $wsh = New-Object -ComObject WScript.Shell
  $null = $wsh.AppActivate($proc.Id)
  Start-Sleep -Milliseconds 600

  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Simple Viewer')
  $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
  if (-not $win) { Write-Output 'NO-WINDOW'; exit 1 }

  $btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  $allBtns = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
  $imgBtn = $null
  foreach ($b in $allBtns) { if ($b.Current.Name -match '条件$' -and $b.Current.Name -match '^\+') { $imgBtn = $b; break } }
  Write-Output ('ADD-COND-BTN: ' + $(if ($imgBtn) { $imgBtn.Current.Name } else { 'NOT-FOUND-(panel closed, expected)' }))

  # open panel
  $idCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'FilterToggleButton')
  $toggle = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCond)
  $toggle.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 1200

  # count wall list items before
  $listCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
  $before = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listCond).Count
  Write-Output ('WALL-LISTITEMS-BEFORE: ' + $before)

  # find "+ condition" button inside opened panel
  $allBtns2 = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
  $addBtn = $null
  foreach ($b in $allBtns2) { if ($b.Current.Name -match '条件' -and $b.Current.Name -match '^\+') { $addBtn = $b; break } }
  if (-not $addBtn) { Write-Output 'NO-ADD-BTN'; }

  function Shot($name) {
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $bmp.Save('D:\Dev\Python\simple_viewer\docs\' + $name, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
  }

  Shot 'verify-a-before-add.png'
  if ($addBtn) {
    $addBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 700
    $after = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listCond).Count
    Write-Output ('WALL-LISTITEMS-AFTER-ADD: ' + $after)
    Shot 'verify-b-after-add.png'
    # find and click matcher toggle row exists (panel content grew)
    $addBtn2 = $null
    foreach ($b in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) { if ($b.Current.Name -match '条件' -and $b.Current.Name -match '^\+') { $addBtn2 = $b; break } }
    Write-Output ('SECOND-ADD-STILL-PRESENT: ' + [bool]$addBtn2)
  }
  Write-Output 'DONE'
}
finally {
  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 600
  Move-Item $backup $settings -Force
  Write-Output 'SETTINGS-RESTORED'
}
