# 单图详情页适配（contain）实机验证：生成纯色夹具 → 启动 viewer → UIA 量 Image 盒 + 截屏量红色带
#  用法：powershell -NoProfile -ExecutionPolicy Bypass -File scripts\fit-verify.ps1 -Exe <viewer.exe 路径>
#  判定基准（期望口径：contain = 整窗画布适配，含放大、保比、长边贴合）：
#   · 图片按原始宽高比落入画布（画布 = 窗口客户区，ImageHost 内缩 Padding 8 DIP）
#   · 约束轴贴合：min((画布宽-16)/图宽, (画布高-16)/图高) 即为缩放系数
#   · 反例指纹（修复前）：图片按原始像素 1:1 显示（100%）→ 小图不放大、四周留白
#  注意：本脚本自身须 DPI 感知（否则 GetClientRect/SetWindowPos 被系统虚拟化，几何量测口径错乱）。
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$OutDir = "$env:TEMP\fit-verify",
    [string[]]$Fixtures = @('small', 'wide', 'tall', 'big'),
    [int]$WinW = 1600,
    [int]$WinH = 1000,
    [int]$SettleMs = 4000
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public struct RECT { public int Left, Top, Right, Bottom; }
public struct POINT { public int X, Y; }
public class FitWin {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
}
"@
[void][FitWin]::SetProcessDPIAware()

# 夹具定义：名字 → 原始像素尺寸（纯红，深色 chrome 背景上直接可量）
$fixtureSpec = @{
    'small' = @{ w = 400;  h = 300  }
    'wide'  = @{ w = 800;  h = 200  }
    'tall'  = @{ w = 600;  h = 3000 }
    'big'   = @{ w = 4000; h = 3000 }
}

function New-Fixture([string]$name) {
    $spec = $fixtureSpec[$name]
    $dir = Join-Path $OutDir $name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $path = Join-Path $dir 'red.png'
    if (-not (Test-Path $path)) {
        $bmp = New-Object System.Drawing.Bitmap($spec.w, $spec.h)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.Clear([System.Drawing.Color]::FromArgb(255, 255, 0, 0))
        $g.Dispose()
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
    }
    return @{ dir = $dir; w = $spec.w; h = $spec.h }
}

function Stop-Viewer { Get-Process viewer -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 700 }

# 带参启动时窗口标题会变成文件名，故按“启动的进程”定位窗口（不用标题 FindWindow）
function Wait-Window($proc, [int]$timeoutMs = 30000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) { return @{ hwnd = [IntPtr]::Zero; note = "process exited $($proc.ExitCode)" } }
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { return @{ hwnd = $proc.MainWindowHandle; note = 'ok' } }
        Start-Sleep -Milliseconds 300
    }
    return @{ hwnd = [IntPtr]::Zero; note = 'timeout' }
}

function Capture([IntPtr]$hwnd, [string]$png) {
    $r = New-Object RECT
    [void][FitWin]::GetWindowRect($hwnd, [ref]$r)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [void][FitWin]::PrintWindow($hwnd, $hdc, 2)   # PW_RENDERFULLCONTENT
    $g.ReleaseHdc($hdc); $g.Dispose()
    $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return @{ w = $w; h = $h }
}

# 量测红色带 bbox（R>190 且 G/B<70）
function Measure-Red([string]$png) {
    $bmp = [System.Drawing.Bitmap]::FromFile($png)
    $rect = New-Object System.Drawing.Rectangle(0, 0, $bmp.Width, $bmp.Height)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = New-Object byte[] ($data.Stride * $bmp.Height)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $w = $data.Width; $h = $data.Height; $stride = $data.Stride
    $bmp.UnlockBits($data); $bmp.Dispose()
    $x0 = [int]::MaxValue; $y0 = [int]::MaxValue; $x1 = -1; $y1 = -1; $count = 0
    for ($y = 0; $y -lt $h; $y++) {
        $row = $y * $stride
        for ($x = 0; $x -lt $w; $x++) {
            $i = $row + $x * 4
            if ($bytes[$i + 2] -gt 190 -and $bytes[$i + 1] -lt 70 -and $bytes[$i] -lt 70) {
                $count++
                if ($x -lt $x0) { $x0 = $x }; if ($x -gt $x1) { $x1 = $x }
                if ($y -lt $y0) { $y0 = $y }; if ($y -gt $y1) { $y1 = $y }
            }
        }
    }
    if ($count -eq 0) { return @{ x0 = -1; y0 = -1; x1 = -1; y1 = -1; count = 0; bw = 0; bh = 0 } }
    return @{ x0 = $x0; y0 = $y0; x1 = $x1; y1 = $y1; count = $count; bw = ($x1 - $x0 + 1); bh = ($y1 - $y0 + 1) }
}

# 量 Image 元素盒（UIA，物理像素；修复前 = 图片盒，修复后 = 画布盒）
function Measure-ImageBox($procId) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if (-not $win) { return $null }
    $ic = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Image)
    $img = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $ic)
    if (-not $img) { return $null }
    $r = $img.Current.BoundingRectangle
    return @{ x = [int]$r.X; y = [int]$r.Y; w = [int]$r.Width; h = [int]$r.Height }
}

foreach ($fx in $Fixtures) {
    $f = New-Fixture $fx
    Stop-Viewer
    $proc = Start-Process -FilePath $Exe -ArgumentList '-d', $f.dir, '-i', '1' -PassThru
    $win = Wait-Window $proc
    $hwnd = $win.hwnd
    if ($hwnd -eq [IntPtr]::Zero) { "$fx : NO-WINDOW $($win.note)"; continue }
    # 固定窗口几何（物理像素，可复现），等布局/重解码安定
    [void][FitWin]::SetWindowPos($hwnd, [IntPtr]::Zero, 40, 40, $WinW, $WinH, 0x0040)
    Start-Sleep -Milliseconds $SettleMs

    $dpi = [FitWin]::GetDpiForWindow($hwnd)
    $s = if ($dpi -gt 0) { $dpi / 96.0 } else { 1.0 }
    $cr = New-Object RECT; [void][FitWin]::GetClientRect($hwnd, [ref]$cr)
    $clientW = $cr.Right - $cr.Left; $clientH = $cr.Bottom - $cr.Top
    $box = Measure-ImageBox $proc.Id

    $png = Join-Path $OutDir ("shot-$fx.png")
    [void](Capture $hwnd $png)
    $m = Measure-Red $png
    # PrintWindow 的位图以窗口左上为原点；红色带坐标要换算到屏幕再比
    $wr = New-Object RECT; [void][FitWin]::GetWindowRect($hwnd, [ref]$wr)

    $canvasDipW = $clientW / $s; $canvasDipH = $clientH / $s
    $fit = [Math]::Min(($canvasDipW - 16) / $f.w, ($canvasDipH - 16) / $f.h)
    $expW = [int][Math]::Round($f.w * $fit * $s); $expH = [int][Math]::Round($f.h * $fit * $s)
    $natW = [int][Math]::Round($f.w * $s); $natH = [int][Math]::Round($f.h * $s)

    "=== fixture=$fx  src=$($f.w)x$($f.h)  dpi=$dpi scale=$s"
    "  window/client(px) = $($wr.Right - $wr.Left)x$($wr.Bottom - $wr.Top) / $clientW x $clientH   canvas(DIP)=$([int]$canvasDipW)x$([int]$canvasDipH)"
    "  expect contain(px) = $expW x $expH   (fit=$([Math]::Round($fit,3)))    expect natural-100%(px) = $natW x $natH"
    if ($box) { "  UIA Image box(px) = $($box.w) x $($box.h) at ($($box.x),$($box.y))" } else { '  UIA Image box = (none)' }
    "  pixel red band(win-local) = ($($m.x0),$($m.y0))-($($m.x1),$($m.y1)) size=$($m.bw)x$($m.bh) area=$($m.count)"
    "  pixel red band(screen)    = ($($m.x0 + $wr.Left),$($m.y0 + $wr.Top))  size=$($m.bw)x$($m.bh)"
    if ($box) {
        "  verdict: boxVsContain W=$([Math]::Round($box.w / $expW,3)) H=$([Math]::Round($box.h / $expH,3))  boxVsNatural W=$([Math]::Round($box.w / $natW,3)) H=$([Math]::Round($box.h / $natH,3))"
    }
    "  verdict: aspect(band)=$([Math]::Round($m.bw / [Math]::Max(1,$m.bh),3)) srcAspect=$([Math]::Round($f.w / $f.h,3))"
}

Stop-Viewer
"shots: $OutDir"
