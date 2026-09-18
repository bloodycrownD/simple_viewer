# Shift+点击合成测试：SendInput 注入 Shift 按下 → 鼠标点击 → Shift 抬起
# 用法: .\shift-click-test.ps1 -X 1140 -Y 748
param(
    [int]$X,
    [int]$Y,
    [switch]$NoShift,
    [long]$Hwnd = 0
)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeDpi {
    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();
}
"@
[NativeDpi]::SetProcessDPIAware() | Out-Null

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeInput {
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct INPUT {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const ushort VK_SHIFT = 0x10;

    public static void KeyDown(ushort vk) {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.ki.wVk = vk;
        var ok = SendInput(1, new INPUT[] { input }, 40);
        if (ok != 1) { throw new Exception("SendInput keydown failed: " + Marshal.GetLastWin32Error()); }
    }

    public static void KeyUp(ushort vk) {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.ki.wVk = vk;
        input.ki.dwFlags = KEYEVENTF_KEYUP;
        var ok = SendInput(1, new INPUT[] { input }, 40);
        if (ok != 1) { throw new Exception("SendInput keyup failed: " + Marshal.GetLastWin32Error()); }
    }
}
"@

# 鼠标用 mouse_event（相对当前光标位置），先定位
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeMouse {
    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    public const uint LEFTDOWN = 0x0002;
    public const uint LEFTUP = 0x0004;
}
"@

# 先把 viewer 窗口提到前台（后台终端宿主会抢前台，导致注入点落在宿主窗口上）
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeWin {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
}
"@
if ($Hwnd -gt 0) {
    $hwnd = [IntPtr]$Hwnd
} else {
    $hwnd = [NativeWin]::FindWindow($null, "Simple Viewer")
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Host "viewer window not found"; exit 1 }
[NativeWin]::ShowWindow($hwnd, 9) | Out-Null   # SW_RESTORE
[NativeWin]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 400

[NativeInput]::SetCursorPos($X, $Y) | Out-Null
Start-Sleep -Milliseconds 50

# SetForegroundWindow 受系统限制可能静默失败：键盘事件只进前台窗口线程。
# 先普通点击一次目标窗口（点击会激活光标下的窗口），确保后续 Shift 键进 viewer 线程。
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeFg {
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
}
"@
if ([NativeFg]::GetForegroundWindow() -ne $hwnd) {
    # 预点击落在窗口内不改变选择的区域（底部状态栏），仅用于激活窗口
    [NativeInput]::SetCursorPos(600, 840) | Out-Null
    Start-Sleep -Milliseconds 50
    [NativeMouse]::mouse_event([NativeMouse]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 20
    [NativeMouse]::mouse_event([NativeMouse]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 400
    if ([NativeFg]::GetForegroundWindow() -ne $hwnd) { Write-Host "WARN: viewer still not foreground" }
    [NativeInput]::SetCursorPos($X, $Y) | Out-Null
    Start-Sleep -Milliseconds 50
}

if (-not $NoShift) {
    [NativeInput]::KeyDown([NativeInput]::VK_SHIFT)
    Start-Sleep -Milliseconds 30
}
[NativeMouse]::mouse_event([NativeMouse]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 20
[NativeMouse]::mouse_event([NativeMouse]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 30
if (-not $NoShift) {
    # Tapped 手势事件在指针抬起后异步派发：Shift 需按住足够久，
    # 否则 keyup 先于 handler 读键盘状态（30ms 曾不够）
    Start-Sleep -Milliseconds 500
    [NativeInput]::KeyUp([NativeInput]::VK_SHIFT)
}
Write-Host "click @($X,$Y) shift=$(-not $NoShift) injected"
