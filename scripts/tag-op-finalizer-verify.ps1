# tag-op-finalizer-crash 验证脚本（2026-09-26）
# 用途：为「打标收尾触发侧栏全量重建 → 画刷裸丢给 GC 终结器」修复提供可复跑的实机证据。
#   -Phase setup   : 造夹具图库（带标签文件名）+ 备份 settings.json
#   -Phase color   : 部署版 v1.0.5（E:\App\Others\viewer\viewer.exe）与本地 Debug 构建同状态截屏，
#                    对侧栏/筛选条逐像素比对（深/浅两主题各一轮，RULE:56 屏幕像素采样口径）。
#                    每主题跑三遍：release / debug / release-对照（同二进制复跑，证明方法本身稳定）。
#   -Phase churn   : Debug 构建开图库 → 全选 → 快捷键打标 → 抓 startup.log 的 sidebar:rebuild 埋点
#                    （brushNew/brushSinceLast/brushCalls/cache）
#   -Phase chain   : 打标链路实机回归（单图加/移除标签、筛选点击、组展开折叠）
#   -Phase restore : 还原 settings.json（务必在最后跑一次）
# 截图口径：PrintWindow(PW_RENDERFULLCONTENT)——屏幕上有其他应用窗口悬浮在左下角，
#           CopyFromScreen 会被遮挡污染（实测：第三方窗口内容混入底部区域、并带自身动画）。
# 依赖：脚本自身目录锚定仓库根（任意机器路径可跑）；UIA 注入走 Invoke（无修饰键分支）。
param(
    [ValidateSet('setup', 'color', 'churn', 'chain', 'restore')]
    [string]$Phase = 'setup'
)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
# 注：C# 片段引用 System.Drawing 必须显式 -ReferencedAssemblies（PS5.1 的 Add-Type 不继承 -AssemblyName 的引用）
Add-Type -ReferencedAssemblies System.Drawing, System.Windows.Forms @"
using System;
using System.Drawing;
using System.Runtime.InteropServices;
public class Win32Shot {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
public class ImgProbe {
  // 比较两图的指定区域，返回 diff=<不同像素数>;total=<总像素数>;first=<前 5 个不同点>
  public static string Diff(string pa, string pb, int x, int y, int w, int h) {
    using (var ba = new Bitmap(pa))
    using (var bb = new Bitmap(pb)) {
      int diff = 0; string first = "";
      for (int j = y; j < y + h; j++) {
        for (int i = x; i < x + w; i++) {
          Color ca = ba.GetPixel(i, j); Color cb = bb.GetPixel(i, j);
          if (ca.ToArgb() != cb.ToArgb()) {
            diff++;
            if (diff <= 5) first += "(" + i + "," + j + ":" + ca.R + "," + ca.G + "," + ca.B + "vs" + cb.R + "," + cb.G + "," + cb.B + ")";
          }
        }
      }
      return "diff=" + diff + ";total=" + (w * h) + ";first=" + first;
    }
  }
  // 取单点 RGB（"R,G,B"）
  public static string Pixel(string p, int x, int y) {
    using (var b = new Bitmap(p)) { Color c = b.GetPixel(x, y); return c.R + "," + c.G + "," + c.B; }
  }
}
"@
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[Win32Shot]::SetProcessDPIAware() | Out-Null

$root = Split-Path $PSScriptRoot -Parent
$debugExe = Join-Path $root 'bin\x64\Debug\net8.0-windows10.0.19041.0\viewer.exe'
$releaseExe = 'E:\App\Others\viewer\viewer.exe'   # 部署版 v1.0.5（只读参照，勿改勿发布）
$settings = Join-Path $env:LocalAppData 'SimpleViewer\settings.json'
$backup = Join-Path $env:TEMP 'sv-tagop-settings-backup.json'
$log = Join-Path $env:LocalAppData 'SimpleViewer\logs\startup.log'
$fixtureView = Join-Path $env:TEMP 'sv-tagop-lib-view'   # 颜色比对用（不被打标改动）
$fixtureTag = Join-Path $env:TEMP 'sv-tagop-lib-tag'     # 打标用（会被改名）
$outDir = Join-Path $env:TEMP 'sv-tagop-shots'
$likeTagId = 'c111c66da6d942dea8200c2b7d2af4aa'          # 配置里「喜欢」的稳定 Id
$winX = 40; $winY = 40; $winW = 1600; $winH = 900

# 窗口内相对区域（PrintWindow 位图坐标系 = 窗口左上角为原点）
$sidebarRegion = @(0, 0, 420, 900)
$topRegion = @(420, 0, 1180, 260)

New-Item -ItemType Directory -Force $outDir | Out-Null

# 夹具文件名（TagSpaces 协议 base[tag tag].ext；未定义区靠「陌生标签/临时标记」——不在配置组内）
$fixtureNames = @(
    'photo_1[喜欢].jpg',
    'photo_2[喜欢 官方图].jpg',
    'photo_3[一般 大水印].jpg',
    'photo_4[灵魂].jpg',
    'photo_5[陌生标签].jpg',
    'photo_6[喜欢 临时标记].jpg',
    'photo_7[喜欢].jpg',
    'photo_8.jpg',
    'photo_9[官方图].jpg',
    'photo_10[喜欢 官方图].jpg'
)

function Write-TestSettings([string]$theme, [string]$libRoot, [bool]$withTagShortcut) {
    $json = Get-Content -LiteralPath $settings -Raw -Encoding UTF8 | ConvertFrom-Json
    $json.preferredTheme = $theme
    $json.lastLibraryRoot = $libRoot
    if ($withTagShortcut) {
        $binding = [pscustomobject]@{
            virtualKey = 'Number1'; modifiers = @(); command = 'applyTag'; targetPath = $null; tagId = $likeTagId
        }
        $json.shortcuts = @($json.shortcuts) + $binding
    }
    $json | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $settings -Encoding UTF8
}

function Start-Viewer([string]$exe) {
    taskkill /IM viewer.exe /F 2>$null | Out-Null
    Start-Sleep -Milliseconds 900
    Start-Process -FilePath $exe | Out-Null
    $proc = $null
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 500
        $proc = Get-Process viewer -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
        if ($proc) { break }
    }
    if (-not $proc) { throw "viewer 主窗口未出现（$exe）" }
    [Win32Shot]::MoveWindow($proc.MainWindowHandle, $winX, $winY, $winW, $winH, $true) | Out-Null
    Start-Sleep -Milliseconds 500
    $wsh = New-Object -ComObject WScript.Shell
    $null = $wsh.AppActivate($proc.Id)
    Start-Sleep -Milliseconds 400
    # 鼠标移出窗口（避免 hover 视觉污染像素比对；左上角外侧）
    [Win32Shot]::SetCursorPos(1695, 20) | Out-Null
    return $proc
}

function Stop-Viewer { taskkill /IM viewer.exe /F 2>$null | Out-Null; Start-Sleep -Milliseconds 700 }

# PrintWindow(PW_RENDERFULLCONTENT=2)：与遮挡无关（屏幕上另有悬浮窗口，CopyFromScreen 会被污染）
function Capture-Window([IntPtr]$hwnd, [string]$path) {
    $r = New-Object Win32Shot+RECT
    [Win32Shot]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [Win32Shot]::PrintWindow($hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc); $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $ok
}

function Wait-Settle([IntPtr]$hwnd, [int]$maxTries = 10) {
    $a = Join-Path $outDir 'settle-a.png'; $b = Join-Path $outDir 'settle-b.png'
    $d = ''
    for ($i = 0; $i -lt $maxTries; $i++) {
        $null = Capture-Window $hwnd $a
        Start-Sleep -Milliseconds 1500
        $null = Capture-Window $hwnd $b
        $d = [ImgProbe]::Diff($a, $b, 0, 0, $winW, $winH)
        if ($d.StartsWith('diff=0;')) { return $d }
    }
    return $d
}

function Get-Win([int]$procId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $cond)
}

function Find-ById($win, [string]$id) {
    $c = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Find-BtnByName($win, [string]$name) {
    $bt = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $nm = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $and = New-Object System.Windows.Automation.AndCondition($bt, $nm)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $and)
}

function Find-BtnByPartialName($win, [string]$text) {
    $bt = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $bt)
    foreach ($el in $all) {
        if ($el.Current.Name -like "*$text*") { return $el }
    }
    return $null
}

# 组头行（模板 Button 无 Name；侧栏内的无名 Button 就是组头，按 Y 排序取第 index 个）
# 判定阈值用窗口相对坐标（窗口原点 $winX/$winY）：Left 落在侧栏列、Top 在工具栏之下、宽度接近侧栏宽
function Find-GroupHeader($win, [int]$index) {
    $bt = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $bt)
    $headers = @()
    foreach ($el in $all) {
        $r = $el.Current.BoundingRectangle
        if ([string]::IsNullOrEmpty($el.Current.Name) `
                -and $r.Left -ge ($winX + 5) -and $r.Left -lt ($winX + 250) `
                -and $r.Top -gt ($winY + 150) `
                -and $r.Width -gt 300 -and $r.Width -lt 420) {
            $headers += ,@($el, $r.Top)
        }
    }
    $sorted = $headers | Sort-Object { $_[1] }
    if ($index -ge $sorted.Count) { return $null }
    return $sorted[$index][0]
}

# 侧栏滚动复位（ScrollViewer 有 ScrollPattern：把滚动位置钉到顶部，消除两次运行间的滚动差异）
function Reset-SidebarScroll($win) {
    $sc = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Pane)
    $panes = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $sc)
    foreach ($el in $panes) {
        $r = $el.Current.BoundingRectangle
        if ($r.Left -lt 200 -and $r.Height -gt 300) {
            $pattern = $null
            if ($el.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern)) {
                $pattern.SetScrollPercent(-1, 0)   # NoScroll, 0%
                return $true
            }
        }
    }
    return $false
}

# 瀑布流滚动（滚动容器 = 内容区那个可滚 Pane；把目标卡片滚进视野，否则卡片虚拟化/离屏无 bounds）
function Set-WaterfallScroll($win, [int]$percent) {
    $sc = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Pane)
    $panes = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $sc)
    foreach ($el in $panes) {
        $r = $el.Current.BoundingRectangle
        if ($r.Left -gt 200 -and $r.Height -gt 300 -and $r.Width -gt 500) {
            $pattern = $null
            if ($el.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern)) {
                $pattern.SetScrollPercent(-1, $percent)
                Start-Sleep -Milliseconds 600
                return $true
            }
        }
    }
    return $false
}

# 焦点中和：把键盘焦点固定到工具栏「全选」按钮——消除「系统焦点框落在哪个元素」的运行间差异
# （实测：UIA Invoke 之后焦点框可能落在组头行等不同元素上，是像素比对的头号假差异源）
function Neutralize-Focus($win) {
    $el = Find-ById $win 'SelectAllButton'
    if (-not $el) { return $false }
    try {
        $el.SetFocus()
        return $true
    } catch {
        return $false
    }
}

# 窗口失活：点窗口右侧桌面空白——WinUI 在失活窗口上不画系统焦点框（SetFocus 归一不可靠：
# 实测同一二进制两轮之间焦点框仍会落在组头行或全选按钮上，因为窗口激活时 WinUI 会自行决定首焦点元素）
function Deactivate-Window {
    [Win32Shot]::SetCursorPos(1700, 1000) | Out-Null
    Start-Sleep -Milliseconds 150
    [Win32Shot]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    [Win32Shot]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 500
}

# 截屏前状态归一：焦点中和 + 侧栏滚动复位 + 窗口失活 + 等悬浮滚动条淡出 + 静置判定
function Prepare-Capture($proc, [IntPtr]$hwnd) {
    $win = Get-Win $proc.Id
    $focused = Neutralize-Focus $win
    $scrolled = Reset-SidebarScroll $win
    Deactivate-Window
    Start-Sleep -Milliseconds 2400   # 程序化滚动会唤出 WinUI 悬浮滚动条，等它淡出
    $settle = Wait-Settle $hwnd
    return ("$settle focus=$focused scroll=$scrolled")
}

function Invoke-El($el) {
    if (-not $el) { return $false }
    $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    return $true
}

# UIA 屏幕坐标 → 窗口内坐标（PrintWindow 位图坐标；窗口原点固定 $winX/$winY）
function Anchor-Of($el) {
    $r = $el.Current.BoundingRectangle
    if ([double]::IsNaN($r.Left) -or [double]::IsInfinity($r.Width) -or $r.Width -le 0) { return $null }
    # 屏幕 → 窗口内（窗口原点固定 $winX/$winY）
    $x = [int](($r.Left + $r.Right) / 2) - $winX
    $y = [int](($r.Top + $r.Bottom) / 2) - $winY
    return @($x, $y)
}

function Send-Key([byte]$vk) {
    [Win32Shot]::keybd_event($vk, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 70
    [Win32Shot]::keybd_event($vk, 0, 2, [UIntPtr]::Zero)   # KEYEVENTF_KEYUP
}

function Double-ClickAt([int]$x, [int]$y) {
    [Win32Shot]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 120
    [Win32Shot]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    [Win32Shot]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 90
    [Win32Shot]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    [Win32Shot]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 300
}

# ==================== setup ====================
if ($Phase -eq 'setup') {
    if (Test-Path -LiteralPath $backup) {
        Write-Output ('STALE-BACKUP 存在（上次运行未收尾）：先还原 ' + $backup)
        Copy-Item -LiteralPath $backup -Destination $settings -Force
    }
    Copy-Item -LiteralPath $settings -Destination $backup -Force
    Write-Output ('settings 备份 → ' + $backup)

    foreach ($dir in @($fixtureView, $fixtureTag)) {
        if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
        New-Item -ItemType Directory -Force $dir | Out-Null
        for ($i = 1; $i -le 10; $i++) {
            $src = Join-Path (Join-Path $root 'test-library') ("photo_$i.jpg")
            Copy-Item -LiteralPath $src -Destination (Join-Path $dir $fixtureNames[$i - 1]) -Force
        }
        Write-Output ('夹具就绪 ' + $dir + ' → ' + ((Get-ChildItem -LiteralPath $dir).Count) + ' 张')
        Get-ChildItem -LiteralPath $dir | ForEach-Object { Write-Output ('  ' + $_.Name) }
    }
}

# ==================== color ====================
if ($Phase -eq 'color') {
    if (-not (Test-Path -LiteralPath $backup)) { throw '先跑 -Phase setup（settings 未备份）' }
    foreach ($theme in @('Dark', 'Light')) {
        Write-TestSettings $theme $fixtureView $false
        Write-Output ("===== 主题 $theme =====")
        # release-对照 = 同二进制复跑（方法稳定性对照：同二进制两轮之间应为 0 差异）
        foreach ($variant in @(
                @{ n = 'release'; exe = $releaseExe },
                @{ n = 'debug'; exe = $debugExe },
                @{ n = 'release2'; exe = $releaseExe })) {
            $proc = Start-Viewer $variant.exe
            $hwnd = $proc.MainWindowHandle
            $settle = Prepare-Capture $proc $hwnd
            Write-Output ("[$theme/$($variant.n)] 静置(A): $settle")

            # 语义锚点（UIA 定位 → 窗口内坐标；每次重建后重新定位，避免用到已回收元素）
            $win = Get-Win $proc.Id
            $anchors = @{}
            $gh1 = Find-GroupHeader $win 0
            if ($gh1) { $a = Anchor-Of $gh1; if ($a) { $anchors['组头行_互斥组'] = $a } }
            $rowInactive = Find-BtnByName $win '官方图'
            if ($rowInactive) { $a = Anchor-Of $rowInactive; if ($a) { $anchors['标签行_未激活'] = $a } }
            $untagged = Find-BtnByName $win '∅'
            if ($untagged) { $a = Anchor-Of $untagged; if ($a) { $anchors['无标签按钮'] = $a } }
            $null = Capture-Window $hwnd (Join-Path $outDir "$theme-$($variant.n)-A.png")
            $anchors | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outDir "$theme-$($variant.n)-A-anchors.json") -Encoding UTF8

            # 状态 B：激活筛选（UIA Invoke 标签行 = 无修饰点击分支）→ 筛选条 chip + 激活行
            $rowLike = Find-BtnByName $win '喜欢'
            if (Invoke-El $rowLike) {
                Start-Sleep -Seconds 2
                $settle = Prepare-Capture $proc $hwnd
                Write-Output ("[$theme/$($variant.n)] 静置(B): $settle")
                $win = Get-Win $proc.Id
                $rowLike = Find-BtnByName $win '喜欢'
                if ($rowLike) { $a = Anchor-Of $rowLike; if ($a) { $anchors['标签行_激活'] = $a } }
                $chipX = Find-BtnByName $win '✕'
                if ($chipX) { $a = Anchor-Of $chipX; if ($a) { $anchors['筛选条chip'] = $a } }
                $bar = Find-BtnByPartialName $win '筛选'
                if ($bar) { $a = Anchor-Of $bar; if ($a) { $anchors['筛选按钮'] = $a } }
            } else { Write-Output 'WARN: 未找到标签行「喜欢」，状态 B 跳过' }
            $null = Capture-Window $hwnd (Join-Path $outDir "$theme-$($variant.n)-B.png")
            $anchors | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outDir "$theme-$($variant.n)-B-anchors.json") -Encoding UTF8

            # 状态 C：折叠「负面标签」组（组头点击）→ 未定义标签区进入视野
            $win = Get-Win $proc.Id
            $gh2 = Find-GroupHeader $win 1
            if (Invoke-El $gh2) {
                Start-Sleep -Seconds 2
                $settle = Prepare-Capture $proc $hwnd
                Write-Output ("[$theme/$($variant.n)] 静置(C): $settle")
                $win = Get-Win $proc.Id
                $undef = Find-ById $win 'UndefinedChip_陌生标签'
                if ($undef) { $a = Anchor-Of $undef; if ($a) { $anchors['未定义chip'] = $a } }
                $undefHeader = Find-ById $win 'UndefinedTagsHeader'
                if ($undefHeader) { $a = Anchor-Of $undefHeader; if ($a) { $anchors['未定义区标题'] = $a } }
                $gh2b = Find-GroupHeader $win 1
                if ($gh2b) { $a = Anchor-Of $gh2b; if ($a) { $anchors['组头行_兼容组_折叠'] = $a } }
            } else { Write-Output 'WARN: 未找到第二个组头，状态 C 跳过' }
            $null = Capture-Window $hwnd (Join-Path $outDir "$theme-$($variant.n)-C.png")
            $anchors | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outDir "$theme-$($variant.n)-C-anchors.json") -Encoding UTF8

            $anchors.GetEnumerator() | Sort-Object Name | ForEach-Object { Write-Output ("  锚点 $($_.Key) = $($_.Value -join ',')") }
            Stop-Viewer
        }

        foreach ($pair in @(@('release', 'debug'), @('release', 'release2'))) {
            foreach ($state in @('A', 'B', 'C')) {
                $pa = Join-Path $outDir "$theme-$($pair[0])-$state.png"
                $pb = Join-Path $outDir "$theme-$($pair[1])-$state.png"
                Write-Output ("[$theme/$state] $($pair[0]) vs $($pair[1]) 侧栏区: " + [ImgProbe]::Diff($pa, $pb, $sidebarRegion[0], $sidebarRegion[1], $sidebarRegion[2], $sidebarRegion[3]))
                Write-Output ("[$theme/$state] $($pair[0]) vs $($pair[1]) 顶栏+筛选条区: " + [ImgProbe]::Diff($pa, $pb, $topRegion[0], $topRegion[1], $topRegion[2], $topRegion[3]))
                Write-Output ("[$theme/$state] $($pair[0]) vs $($pair[1]) 整窗: " + [ImgProbe]::Diff($pa, $pb, 0, 0, $winW, $winH))
            }
        }
        # 语义锚点逐点 RGB 比对（release vs debug）；锚点归属状态（累计字典按状态切片）
        $stateKeys = @{
            A = @('组头行_互斥组', '标签行_未激活', '无标签按钮')
            B = @('标签行_激活', '筛选条chip', '筛选按钮')
            C = @('未定义chip', '未定义区标题', '组头行_兼容组_折叠')
        }
        foreach ($state in @('A', 'B', 'C')) {
            $fa = Join-Path $outDir "$theme-release-$state-anchors.json"
            $fb = Join-Path $outDir "$theme-debug-$state-anchors.json"
            if (-not (Test-Path -LiteralPath $fa) -or -not (Test-Path -LiteralPath $fb)) { continue }
            $ja = Get-Content -LiteralPath $fa -Raw -Encoding UTF8 | ConvertFrom-Json
            $jb = Get-Content -LiteralPath $fb -Raw -Encoding UTF8 | ConvertFrom-Json
            $pa = Join-Path $outDir "$theme-release-$state.png"
            $pb = Join-Path $outDir "$theme-debug-$state.png"
            foreach ($key in $stateKeys[$state]) {
                if (-not $ja.PSObject.Properties.Name.Contains($key)) { continue }
                if (-not $jb.PSObject.Properties.Name.Contains($key)) { continue }
                $x = $ja.$key[0]; $y = $ja.$key[1]
                $rgbA = [ImgProbe]::Pixel($pa, $x, $y)
                $rgbB = [ImgProbe]::Pixel($pb, $x, $y)
                $flag = if ($rgbA -eq $rgbB) { 'SAME' } else { 'DIFF' }
                Write-Output ("  [$theme/$state] $key @($x,$y) release=$rgbA debug=$rgbB → $flag")
            }
        }
    }
}

# ==================== churn ====================
if ($Phase -eq 'churn') {
    if (-not (Test-Path -LiteralPath $backup)) { throw '先跑 -Phase setup（settings 未备份）' }
    Write-TestSettings 'Dark' $fixtureTag $true
    $proc = Start-Viewer $debugExe
    $hwnd = $proc.MainWindowHandle
    $settle = Wait-Settle $hwnd
    Write-Output ("静置判定: $settle")
    $win = Get-Win $proc.Id

    $lines0 = @(Get-Content -LiteralPath $log -ErrorAction SilentlyContinue).Count
    Write-Output ("日志基线行数 = $lines0")

    # 全选（UIA Invoke）→ 快捷键 Number1 批量打标
    Invoke-El (Find-ById $win 'SelectAllButton') | Out-Null
    Start-Sleep -Seconds 1
    $null = Capture-Window $hwnd (Join-Path $outDir 'churn-before-tag.png')
    Write-Output '--- 打标前目录 ---'
    Get-ChildItem -LiteralPath $fixtureTag | ForEach-Object { Write-Output ('  ' + $_.Name) }

    Send-Key 0x31   # '1'
    Start-Sleep -Seconds 8
    $lines1 = @(Get-Content -LiteralPath $log).Count
    Write-Output "--- 打标后新增日志（行 $($lines0+1)..$lines1） ---"
    Get-Content -LiteralPath $log | Select-Object -Skip $lines0 | Where-Object { $_ -match 'sidebar:rebuild|churn|打标签' } |
        ForEach-Object { Write-Output ('  ' + $_) }
    Write-Output '--- 打标后目录 ---'
    Get-ChildItem -LiteralPath $fixtureTag | ForEach-Object { Write-Output ('  ' + $_.Name) }
    $null = Capture-Window $hwnd (Join-Path $outDir 'churn-after-tag.png')

    # 第二次重建：组头点击（折叠）
    $win = Get-Win $proc.Id
    $gh = Find-GroupHeader $win 0
    Invoke-El $gh | Out-Null
    Start-Sleep -Seconds 3
    $lines2 = @(Get-Content -LiteralPath $log).Count
    Write-Output "--- 组折叠后新增日志（行 $($lines1+1)..$lines2） ---"
    Get-Content -LiteralPath $log | Select-Object -Skip $lines1 | Where-Object { $_ -match 'sidebar:rebuild|churn' } |
        ForEach-Object { Write-Output ('  ' + $_) }
    $null = Capture-Window $hwnd (Join-Path $outDir 'churn-group-collapsed.png')

    # 第三次重建：再点一次恢复展开
    $win = Get-Win $proc.Id
    $gh = Find-GroupHeader $win 0
    Invoke-El $gh | Out-Null
    Start-Sleep -Seconds 3
    $lines3 = @(Get-Content -LiteralPath $log).Count
    Write-Output "--- 组展开后新增日志（行 $($lines2+1)..$lines3） ---"
    Get-Content -LiteralPath $log | Select-Object -Skip $lines2 | Where-Object { $_ -match 'sidebar:rebuild|churn' } |
        ForEach-Object { Write-Output ('  ' + $_) }
    $null = Capture-Window $hwnd (Join-Path $outDir 'churn-group-expanded.png')

    Write-Output '--- 本轮全部 sidebar:rebuild 行 ---'
    Get-Content -LiteralPath $log | Select-Object -Skip $lines0 | Where-Object { $_ -match 'sidebar:rebuild|churn 告警' } |
        ForEach-Object { Write-Output ('  ' + $_) }
    Stop-Viewer
}

# ==================== chain ====================
if ($Phase -eq 'chain') {
    if (-not (Test-Path -LiteralPath $backup)) { throw '先跑 -Phase setup（settings 未备份）' }
    Write-TestSettings 'Dark' $fixtureTag $true
    $proc = Start-Viewer $debugExe
    $hwnd = $proc.MainWindowHandle
    $settle = Wait-Settle $hwnd
    Write-Output ("静置判定: $settle")
    $win = Get-Win $proc.Id

    # 1) 单图态：双击无标签卡片 photo_8.jpg → 快捷键加标签 → 右栏 chip ✕ 移除
    # 先把瀑布流滚到底（photo_8 在 10 张里的第 8 位，默认视口外 → UIA bounds 为空无法点击）
    $null = Set-WaterfallScroll $win 100
    $win = Get-Win $proc.Id
    $card = Find-BtnByName $win 'photo_8.jpg'
    if (-not $card) { Write-Output 'FAIL: 未找到卡片 photo_8.jpg' } else {
        $r = $card.Current.BoundingRectangle
        Double-ClickAt ([int](($r.Left + $r.Right) / 2)) ([int](($r.Top + $r.Bottom) / 2))
        Start-Sleep -Seconds 3
        $null = Capture-Window $hwnd (Join-Path $outDir 'chain-single-before.png')
        Write-Output '--- 单图态：加标签前（photo_8*） ---'
        Get-ChildItem -LiteralPath $fixtureTag -Filter 'photo_8*' | ForEach-Object { Write-Output ('  ' + $_.Name) }

        Send-Key 0x31
        Start-Sleep -Seconds 5
        $null = Capture-Window $hwnd (Join-Path $outDir 'chain-single-tagged.png')
        Write-Output '--- 单图态：加标签后（photo_8*） ---'
        Get-ChildItem -LiteralPath $fixtureTag -Filter 'photo_8*' | ForEach-Object { Write-Output ('  ' + $_.Name) }
        Write-Output ('右栏 chip 区域像素差异（打标前 vs 打标后）: ' + [ImgProbe]::Diff(
                (Join-Path $outDir 'chain-single-before.png'), (Join-Path $outDir 'chain-single-tagged.png'), 1140, 160, 460, 540))

        # 右栏 ✕ 移除
        $win = Get-Win $proc.Id
        $remove = Find-BtnByName $win '✕'
        if (Invoke-El $remove) {
            Start-Sleep -Seconds 5
            $null = Capture-Window $hwnd (Join-Path $outDir 'chain-single-removed.png')
            Write-Output '--- 单图态：移除后（photo_8*） ---'
            Get-ChildItem -LiteralPath $fixtureTag -Filter 'photo_8*' | ForEach-Object { Write-Output ('  ' + $_.Name) }
            Write-Output ('右栏 chip 区域像素差异（打标后 vs 移除后）: ' + [ImgProbe]::Diff(
                    (Join-Path $outDir 'chain-single-tagged.png'), (Join-Path $outDir 'chain-single-removed.png'), 1140, 160, 460, 540))
        } else { Write-Output 'FAIL: 未找到右栏 chip ✕ 按钮' }
    }

    # 2) 回图库：顶部信息横条返回入口（唯一返回入口）
    $win = Get-Win $proc.Id
    $back = Find-BtnByPartialName $win '返回图库'
    if (Invoke-El $back) { Start-Sleep -Seconds 2; Write-Output '返回图库：已点击' }
    else { Write-Output 'WARN: 未找到返回图库按钮' }
    $win = Get-Win $proc.Id

    # 3) 标签筛选点击 → 侧栏激活行 + 筛选条 chip
    $rowLike = Find-BtnByName $win '喜欢'
    if (Invoke-El $rowLike) {
        Start-Sleep -Seconds 3
        $null = Capture-Window $hwnd (Join-Path $outDir 'chain-filter-on.png')
        Write-Output '筛选点击：已激活（截图 chain-filter-on.png）'
        $win = Get-Win $proc.Id
        $toggle = Find-ById $win 'FilterToggleButton'
        Write-Output ('筛选按钮存在: ' + [bool]$toggle)
        $chipX = Find-BtnByName $win '✕'
        Write-Output ('筛选条 chip ✕ 存在: ' + [bool]$chipX)
    } else { Write-Output 'FAIL: 未找到标签行「喜欢」' }

    # 4) 组展开折叠
    $win = Get-Win $proc.Id
    $gh = Find-GroupHeader $win 0
    if (Invoke-El $gh) {
        Start-Sleep -Seconds 2
        $null = Capture-Window $hwnd (Join-Path $outDir 'chain-group-collapsed.png')
        $win = Get-Win $proc.Id
        $gh = Find-GroupHeader $win 0
        Invoke-El $gh | Out-Null
        Start-Sleep -Seconds 2
        $null = Capture-Window $hwnd (Join-Path $outDir 'chain-group-expanded.png')
        Write-Output ('组折叠/展开像素差异: ' + [ImgProbe]::Diff(
                (Join-Path $outDir 'chain-group-collapsed.png'), (Join-Path $outDir 'chain-group-expanded.png'), 0, 0, 420, 900))
    } else { Write-Output 'FAIL: 未找到组头按钮' }

    Write-Output '--- 本轮 sidebar:rebuild 行 ---'
    Get-Content -LiteralPath $log | Where-Object { $_ -match 'sidebar:rebuild|churn 告警' } |
        Select-Object -Last 12 | ForEach-Object { Write-Output ('  ' + $_) }
    Stop-Viewer
}

# ==================== restore ====================
if ($Phase -eq 'restore') {
    Stop-Viewer
    if (Test-Path -LiteralPath $backup) {
        Copy-Item -LiteralPath $backup -Destination $settings -Force
        Write-Output ('settings 已还原 ← ' + $backup)
    } else {
        Write-Output 'WARN: 无备份可还原'
    }
    Write-Output '夹具保留（临时目录，供复跑）：'
    Write-Output ('  ' + $fixtureView)
    Write-Output ('  ' + $fixtureTag)
    Write-Output ('  ' + $outDir)
}
