# probe-strip-text.ps1 - 探查单图顶部横条与右栏文件名文本实际渲染
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# qa/C-1 以脚本自身目录（scripts\）锚定仓库根推导 exe 路径，仓库克隆到任意路径/机器可用
$exe = Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = $settings + '.bak-pr'
# qa/B-1 备份前置守卫：残留备份 = 上次运行中途崩溃（finally 未执行）——先还原再重新备份，
# 防止把已被污染的 settings 当作新备份源、唯一好备份被删/覆盖
if (Test-Path $backup) {
  Move-Item $backup $settings -Force
  Write-Output ('STALE-BACKUP-RESTORED: ' + $backup + ' 已还原为 settings，随后重新备份')
}
Copy-Item $settings $backup -Force
$libDir = Join-Path $env:TEMP 'sv-verify-lib4'

try {
  $json = Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json
  $json.LastLibraryRoot = $libDir
  $json.shortcuts = @()
  $json.tagGroups = @()
  $json | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding UTF8

  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 900
  Start-Process $exe -ArgumentList @('-d', $libDir, '-i', '1')
  Start-Sleep -Seconds 8

  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Simple Viewer')
  $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
  $txtCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
  $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $txtCond)
  Write-Output ('TEXT-ELEMENTS: ' + $all.Count)
  foreach ($t in $all) {
    $r = $t.Current.BoundingRectangle
    if ($r.Height -gt 0) {
      Write-Output ('  "' + $t.Current.Name + '" @ ' + [int]$r.X + ',' + [int]$r.Y + ' ' + [int]$r.Width + 'x' + [int]$r.Height)
    }
  }
  Write-Output 'DONE'
}
finally {
  taskkill /IM viewer.exe /F 2>$null | Out-Null
  Start-Sleep -Milliseconds 500
  Move-Item $backup $settings -Force
  Write-Output 'RESTORED'
}
