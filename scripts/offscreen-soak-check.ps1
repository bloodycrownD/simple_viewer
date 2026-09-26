# 屏幕外实机复核（xaml-finalizer-residuals）：窗口放到桌面之外、绝不 SetForegroundWindow。
# 断言：图库缩略图渲染（池化路径）→ 滚动换帧 → 侧栏筛选点击换墙 → 日志埋点与异常 → 事件日志无新崩溃。
param(
    [string]$Exe = 'D:\Dev\Python\simple_viewer\bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe',
    [int]$SettleMs = 7000,
    [string]$ShotDir = "$env:TEMP\sv-offscreen"
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Drawing;
using System.Runtime.InteropServices;
public struct RECT { public int Left, Top, Right, Bottom; }
public class OffWin {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  public static string Shot(IntPtr h, string path) {
    RECT r; GetWindowRect(h, out r);
    int w = r.Right - r.Left, hh = r.Bottom - r.Top;
    using (var bmp = new Bitmap(w, hh)) using (var g = Graphics.FromImage(bmp)) {
      IntPtr dc = g.GetHdc(); bool ok = PrintWindow(h, dc, 2); g.ReleaseHdc(dc);
      bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
      return "printOk=" + ok + " size=" + w + "x" + hh + " at(" + r.Left + "," + r.Top + ")";
    }
  }
  public static string NonBg(string p, int x, int y, int w, int h, int step) {
    using (var bmp = new Bitmap(p)) {
      int tot = 0, non = 0;
      for (int j = y; j < Math.Min(y + h, bmp.Height); j += step)
        for (int i = x; i < Math.Min(x + w, bmp.Width); i += step) {
          Color c = bmp.GetPixel(i, j); tot++;
          bool bg = Math.Abs(c.R - 39) < 16 && Math.Abs(c.G - 39) < 16 && Math.Abs(c.B - 46) < 16;
          if (!bg) non++;
        }
      return "canvasNonBg=" + non + "/" + tot + " ratio=" + (tot > 0 ? Math.Round((double)non / tot, 3).ToString() : "0");
    }
  }
  public static string Diff(string pa, string pb, int step) {
    using (var ba = new Bitmap(pa)) using (var bb = new Bitmap(pb)) {
      if (ba.Width != bb.Width || ba.Height != bb.Height) return "SIZE-MISMATCH";
      int d = 0, t = 0;
      for (int j = 0; j < ba.Height; j += step) for (int i = 0; i < ba.Width; i += step) { t++; if (ba.GetPixel(i, j).ToArgb() != bb.GetPixel(i, j).ToArgb()) d++; }
      return "diff=" + d + "/" + t;
    }
  }
}
"@
[void][OffWin]::SetProcessDPIAware()
New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null

function Stop-Viewer { Get-Process viewer -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 800 }
Stop-Viewer

'=== 夹具图库与 settings 备份 ==='
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'tag-op-finalizer-verify.ps1') -Phase setup 2>&1 | Select-Object -Last 6

'=== 启动（随后立即移到桌面之外）==='
$proc = Start-Process -FilePath $Exe -PassThru
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 250
    $proc.Refresh()
    if ($proc.HasExited) { "PROCESS EXITED $($proc.ExitCode)"; exit 1 }
    if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
}
$hwnd = $proc.MainWindowHandle
# 移到主屏右侧之外的空白区（GetSystemMetrics(0) = 主屏宽；+2000 稳稳在屏幕外）
$x = [OffWin]::GetSystemMetrics(0) + 2000
[void][OffWin]::SetWindowPos($hwnd, [IntPtr]::Zero, $x, 200, 1500, 950, 0x0010)  # SWP_NOACTIVATE
Start-Sleep -Milliseconds 500
$r = New-Object RECT; [void][OffWin]::GetWindowRect($hwnd, [ref]$r)
"位置确认：($($r.Left),$($r.Top)) - ($($r.Right),$($r.Bottom))  主屏宽=$([OffWin]::GetSystemMetrics(0))"
Start-Sleep -Milliseconds $SettleMs

$shotA = Join-Path $ShotDir 'gallery-a.png'
'=== 截图 A（图库首屏）==='
[OffWin]::Shot($hwnd, $shotA)
[OffWin]::NonBg($shotA, 440, 120, 1000, 780, 4)

# UIA 会话
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { 'NO-UIA-WINDOW'; Stop-Viewer; exit 1 }

'=== 滚动到底（UIA ScrollPattern，无需焦点）==='
$scrollOk = $false
foreach ($el in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
    $pat = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$pat)) {
        try { $pat.SetScrollPercent(-1, 100); $scrollOk = $true; break } catch { }
    }
}
"scrollPattern=$scrollOk"
Start-Sleep -Milliseconds 3000
$shotB = Join-Path $ShotDir 'gallery-b.png'
[OffWin]::Shot($hwnd, $shotB)
[OffWin]::Diff($shotA, $shotB, 6)

'=== 点侧栏标签（UIA Invoke）==='
$clicked = ''
foreach ($name in @('喜欢', '灵魂', '一般', '官方图', '删除')) {
    $c = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)))
    $b = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    if ($b) {
        $ip = $null
        if ($b.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$ip)) {
            try { $ip.Invoke(); $clicked = $name; break } catch { }
        }
    }
}
"clickedTag=[$clicked]"
Start-Sleep -Milliseconds 3000
$shotC = Join-Path $ShotDir 'gallery-c.png'
[OffWin]::Shot($hwnd, $shotC)
[OffWin]::Diff($shotB, $shotC, 6)

'=== 进程存活 ==='
$proc.Refresh(); "alive=$(-not $proc.HasExited)"

'=== startup.log 埋点与异常 ==='
$log = Join-Path $env:LOCALAPPDATA 'SimpleViewer\logs\startup.log'
$lines = Get-Content -LiteralPath $log -Encoding UTF8
$recent = $lines | Where-Object { $_ -match '^\[2026-09-26 (1[7-9]|2[0-3]):' -or $_ -match '^\[2026-09-27' }
$recent | Where-Object { $_ -match 'retire:|pool:|sidebar:rebuild' } | Select-Object -Last 12 | ForEach-Object { '  ' + $_ }
'  --- 近期异常行 ---'
$errs = $lines | Where-Object { $_ -match '失败|异常|错误|告警' } | Select-Object -Last 6
if ($errs) { $errs | ForEach-Object { '  ' + $_ } } else { '  (无)' }

'=== 事件日志（近 20 分钟是否有 viewer 崩溃）==='
$ev = Get-WinEvent -FilterHashtable @{ LogName='Application'; ProviderName='Application Error'; StartTime=(Get-Date).AddMinutes(-20) } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match 'viewer\.exe' }
if ($ev) { $ev | ForEach-Object { '  CRASH: ' + $_.TimeCreated + ' ' + ($_.Message -split "`r?`n")[3] } } else { '  (无新崩溃)' }

Stop-Viewer
'=== 还原 settings ==='
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'tag-op-finalizer-verify.ps1') -Phase restore 2>&1 | Select-Object -Last 3
"shots: $ShotDir"
