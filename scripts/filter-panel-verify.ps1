# filter panel verify (round 2): short window + panel + expand values + screenshot
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W4 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
}
"@
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[W4]::SetProcessDPIAware() | Out-Null

# qa/C-1 以脚本自身目录（scripts\）锚定仓库根推导 exe 与输出目录，仓库克隆到任意路径/机器可用
$exe = Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$docsDir = Join-Path $PSScriptRoot '..\docs'
$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = $settings + '.bak2'
# qa/B-1 备份前置守卫：残留备份 = 上次运行中途崩溃（finally 未执行）——先还原再重新备份，
# 防止把已被污染的 settings 当作新备份源、唯一好备份被删/覆盖
if (Test-Path $backup) {
  Move-Item $backup $settings -Force
  Write-Output ('STALE-BACKUP-RESTORED: ' + $backup + ' 已还原为 settings，随后重新备份')
}
Copy-Item $settings $backup -Force

function FindId($win, $id) {
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Shot($name) {
  $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
  $bmp.Save((Join-Path $docsDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
}

try {
  $json = Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json
  $json.LastLibraryRoot = 'D:\Dev\Python\simple_viewer\test-library'
  $json | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding UTF8

  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 900
  Start-Process $exe
  Start-Sleep -Seconds 9
  $proc = Get-Process viewer -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  # short window like user's screenshot: 1350x780 physical (~900x520 logical @150%)
  [W4]::MoveWindow($proc.MainWindowHandle, 60, 60, 1350, 780, $true) | Out-Null
  Start-Sleep -Milliseconds 400
  $wsh = New-Object -ComObject WScript.Shell
  $null = $wsh.AppActivate($proc.Id)
  Start-Sleep -Milliseconds 600

  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Simple Viewer')
  $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
  if (-not $win) { Write-Output 'NO-WINDOW'; exit 1 }

  # open panel
  $toggle = FindId $win 'FilterToggleButton'
  $toggle.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 1100

  # add one condition (now with AutomationId)
  $addCond = FindId $win 'FilterAddCondButton'
  if (-not $addCond) { Write-Output 'NO-ADD-COND' } else {
    $addCond.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 500
    # expand value chooser
    $addValue = FindId $win 'FilterAddValueButton'
    if ($addValue) {
      $addValue.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
      Start-Sleep -Milliseconds 600
      Write-Output 'VALUE-EXPANDED'
    } else { Write-Output 'NO-ADD-VALUE' }
  }
  # second condition row (to grow height further)
  $addCond2 = FindId $win 'FilterAddCondButton'
  if ($addCond2) { $addCond2.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 400 }

  # measure flyout
  $fly = FindId $win 'TagFilterFlyout'
  if ($fly) {
    $r = $fly.Current.BoundingRectangle
    Write-Output ('FLYOUT-RECT: ' + [int]$r.Width + 'x' + [int]$r.Height + ' @' + [int]$r.X + ',' + [int]$r.Y + ' (700dip@150% = 1050 wide)')
  }
  Shot 'verify-c-panel-expanded.png'
  Write-Output 'DONE'
}
finally {
  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 600
  Move-Item $backup $settings -Force
  Write-Output 'SETTINGS-RESTORED'
}
