# Probe the EXACT decode pipeline of ImageLoaderService.LoadAsync for every image:
# GetSoftwareBitmapAsync(Bgra8, Premultiplied, identity transform, RespectExif, DoNotColorManage)
# and report the OUTPUT BitmapPixelFormat / BitmapAlphaMode / dims.
# Any combo other than Bgra8/Premultiplied = SetBitmapAsync E_INVALIDARG smoking gun.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File probe-decode-alpha.ps1 <libraryRoot>

param([Parameter(Mandatory = $true)][string]$LibraryRoot)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Runtime.WindowsRuntime

$null = [Windows.Storage.Streams.FileRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime]
$null = [Windows.Storage.Streams.IRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapPixelFormat, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapAlphaMode, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.ExifOrientationMode, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.ColorManagementMode, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapTransform, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics.Imaging, ContentType = WindowsRuntime]

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() |
    Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]

function AwaitOp($op, $resultType) {
    $task = $asTaskGeneric.MakeGenericMethod($resultType).Invoke($null, @($op))
    if (-not $task.Wait(30000)) { throw "async wait timeout" }
    return $task.Result
}

$files = Get-ChildItem $LibraryRoot -Recurse -File |
    Where-Object { $_.Extension -match '^\.(png|jpg|jpeg|gif|webp|bmp|tiff|tif|wdp)$' } |
    Sort-Object FullName
"probing $($files.Count) files with exact LoadAsync parameters ..."

$combos = @{}
$bad = New-Object System.Collections.Generic.List[string]
$i = 0

foreach ($f in $files) {
    $i++
    try {
        $stream = AwaitOp ([Windows.Storage.Streams.FileRandomAccessStream]::OpenAsync($f.FullName, 'Read')) ([Windows.Storage.Streams.IRandomAccessStream])
        try {
            $decoder = AwaitOp ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
            $transform = [Windows.Graphics.Imaging.BitmapTransform, Windows.Graphics.Imaging, ContentType = WindowsRuntime]::new()
            $op = $decoder.GetSoftwareBitmapAsync(
                [Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8,
                [Windows.Graphics.Imaging.BitmapAlphaMode]::Premultiplied,
                $transform,
                [Windows.Graphics.Imaging.ExifOrientationMode]::RespectExifOrientation,
                [Windows.Graphics.Imaging.ColorManagementMode]::DoNotColorManage)
            $bmp = AwaitOp $op ([Windows.Graphics.Imaging.SoftwareBitmap])
            $combo = "$($bmp.BitmapPixelFormat)/$($bmp.BitmapAlphaMode)"
            if (-not $combos.ContainsKey($combo)) { $combos[$combo] = 0 }
            $combos[$combo]++
            if ("$($bmp.BitmapPixelFormat)" -ne 'Bgra8' -or "$($bmp.BitmapAlphaMode)" -ne 'Premultiplied' -or $bmp.PixelWidth -le 0 -or $bmp.PixelHeight -le 0) {
                $bad.Add("$combo $($bmp.PixelWidth)x$($bmp.PixelHeight) $($f.FullName)")
            }
            $bmp.Dispose()
        }
        finally { $stream.Dispose() }
    }
    catch {
        $bad.Add("THREW $($_.Exception.GetBaseException().Message.Trim()) $($f.FullName)")
    }
    if ($i % 50 -eq 0) { "... $i / $($files.Count)" }
}

"--- output format/alpha distribution ---"
$combos.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { "$($_.Key): $($_.Value)" }
"--- BAD (would fail SetBitmapAsync) ---"
if ($bad.Count -eq 0) { "<none - all outputs Bgra8/Premultiplied/positive>" } else { $bad | ForEach-Object { $_ } }
