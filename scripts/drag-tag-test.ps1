# 真实鼠标拖拽走查脚本（tag-interaction-rework）：瀑布流卡片 → 侧栏标签树行
# 输入：-FromX/-FromY 起点（卡片中心）、-ToX/-ToY 终点（标签行中心，全局屏幕点）
# 约束（RULE SendInput 三坑）：INPUT 结构 40 字节联合体对齐；目标窗口须前台；SetProcessDPIAware。
param(
    [int]$FromX,
    [int]$FromY,
    [int]$ToX,
    [int]$ToY,
    [int]$Steps = 15,
    [int]$StepDelayMs = 20
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NativeDrag {
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT {
        public uint type;
        public MOUSEINPUT mi;
    }
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);
}
'@

[NativeDrag]::SetProcessDPIAware() | Out-Null

function Send-MouseEvent([uint32]$flags, [int]$x, [int]$y) {
    $input = New-Object NativeDrag+INPUT
    $input.type = 0  # INPUT_MOUSE
    $input.mi.dwFlags = $flags
    $input.mi.dx = $x
    $input.mi.dy = $y
    $size = [Runtime.InteropServices.Marshal]::SizeOf([type][NativeDrag+INPUT])
    if ($size -ne 40) { throw "INPUT 结构大小 $size 非 40 字节（联合体对齐错误）" }
    [void][NativeDrag]::SendInput(1, @($input), $size)
}

# 前置：光标落起点并按下（SetCursorPos 在 DPI aware 进程下接受物理坐标，绕开绝对坐标归一化的缩放歧义）
[NativeDrag]::SetCursorPos($FromX, $FromY) | Out-Null
Start-Sleep -Milliseconds 120
Send-MouseEvent 0x8002 $FromX $FromY  # MOUSEEVENTF_LEFTDOWN | ABSOLUTE（落点坐标由 SetCursorPos 保证）
Start-Sleep -Milliseconds 150          # 按住停顿（拖拽手势识别）

# 相对移动（MOUSEEVENTF_MOVE dx/dy 为物理像素增量）：分步过系统拖拽阈值并模拟真实节奏
$stepDx = [math]::Round(($ToX - $FromX) / [double]$Steps)
$stepDy = [math]::Round(($ToY - $FromY) / [double]$Steps)
for ($i = 1; $i -le $Steps; $i++) {
    Send-MouseEvent 0x0001 $stepDx $stepDy   # MOUSEEVENTF_MOVE（相对）
    Start-Sleep -Milliseconds $StepDelayMs
}

Start-Sleep -Milliseconds 200           # 停留在目标上（DragOver 高亮窗口）
Send-MouseEvent 0x0004 $ToX $ToY        # MOUSEEVENTF_LEFTUP
Write-Output "拖拽完成：($FromX,$FromY) -> ($ToX,$ToY)"
