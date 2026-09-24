# Dump viewer window UIA tree: control types + names (two levels deep).
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Simple Viewer')
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { 'NO-WINDOW'; exit 1 }
$btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$buttons = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
"buttons: $($buttons.Count)"
foreach ($b in $buttons) {
  $n = $b.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
  $off = $b.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement].BoundingRectangleProperty)
  "  [$n]"
}
$txtCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
$texts = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $txtCond)
"texts: $($texts.Count) (first 10)"
foreach ($t in ($texts | Select-Object -First 10)) {
  $n = $t.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
  "  text=[$n]"
}
$imgCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Image)
$images = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $imgCond)
"images: $($images.Count)"
