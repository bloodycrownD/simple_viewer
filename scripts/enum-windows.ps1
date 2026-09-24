# Enumerate viewer processes + windows titled *Simple*.
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class WinEnum {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
  public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
}
"@
Get-Process viewer -ErrorAction SilentlyContinue | ForEach-Object {
  "pid=$($_.Id) hwnd=$($_.MainWindowHandle) start=$($_.StartTime.ToString('HH:mm:ss')) responding=$($_.Responding) title=[$($_.MainWindowTitle)]"
}
"--- windows titled Simple ---"
$found = New-Object System.Collections.Generic.List[string]
$cb = {
  param($h, $l)
  $sb = New-Object System.Text.StringBuilder 256
  [WinEnum]::GetWindowText($h, $sb, 256) | Out-Null
  $t = $sb.ToString()
  if ($t -match 'Simple') {
    $p = [uint32]0
    [WinEnum]::GetWindowThreadProcessId($h, [ref]$p) | Out-Null
    $found.Add("hwnd=$h pid=$p visible=$([WinEnum]::IsWindowVisible($h)) title=[$t]")
  }
  return $true
}
[WinEnum]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
$found
