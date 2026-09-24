# Scan native WIC pixel formats for all images in a library; for non-Bgra8 sources,
# test what GetSoftwareBitmapAsync(Bgra8, Premultiplied, RespectExif, DoNotColorManage)
# actually returns (the suspected silent-format-mismatch behind SetBitmapAsync E_INVALIDARG).
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scan-pixel-format.ps1 <libraryRoot>

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

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() |
    Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]

function AwaitOp($op, $resultType) {
    $task = $asTaskGeneric.MakeGenericMethod($resultType).Invoke($null, @($op))
    if (-not $task.Wait(60000)) { throw "async wait timeout" }
    return $task.Result
}

$files = Get-ChildItem $LibraryRoot -Recurse -File |
    Where-Object { $_.Extension -match '^\.(png|jpg|jpeg|gif|webp|bmp|tiff|tif|wdp)$' } |
    Sort-Object FullName
"scanning $($files.Count) files ..."

$formatGroups = @{}
$oddities = New-Object System.Collections.Generic.List[string]
$convertedResults = New-Object System.Collections.Generic.List[string]

foreach ($f in $files) {
    try {
        $stream = AwaitOp ([Windows.Storage.Streams.FileRandomAccessStream]::OpenAsync($f.FullName, 'Read')) ([Windows.Storage.Streams.IRandomAccessStream])
        try {
            $decoder = AwaitOp ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
            $native = [string]$decoder.BitmapPixelFormat
            $dim = "$($decoder.PixelWidth)x$($decoder.PixelHeight)"
            if (-not $formatGroups.ContainsKey($native)) { $formatGroups[$native] = 0 }
            $formatGroups[$native]++
            if ($native -ne 'Bgra8' -and $native -ne 'Rgba8') {
                $oddities.Add("$native | $dim | $($f.FullName)")
                # money test: explicit Bgra8+Premultiplied request, same params as ImageLoaderService.LoadAsync
                $transform = [Windows.Graphics.Imaging.BitmapTransform, Windows.Graphics.Imaging, ContentType = WindowsRuntime]::new()
                $op = $decoder.GetSoftwareBitmapAsync(
                    [Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8,
                    [Windows.Graphics.Imaging.BitmapAlphaMode]::Premultiplied,
                    $transform,
                    [Windows.Graphics.Imaging.ExifOrientationMode]::RespectExifOrientation,
                    [Windows.Graphics.Imaging.ColorManagementMode]::DoNotColorManage)
                try {
                    $bmp = AwaitOp $op ([Windows.Graphics.Imaging.SoftwareBitmap])
                    $got = "$($bmp.BitmapPixelFormat)/$($bmp.BitmapAlphaMode)"
                    $convertedResults.Add("REQ Bgra8/Premultiplied -> GOT $got | $($f.FullName)")
                    $bmp.Dispose()
                }
                catch {
                    $convertedResults.Add("REQ Bgra8/Premultiplied -> THREW $($_.Exception.GetBaseException().Message.Trim()) | $($f.FullName)")
                }
            }
        }
        finally { $stream.Dispose() }
    }
    catch {
        $oddities.Add("ERROR $($_.Exception.GetBaseException().Message.Trim()) | $($f.FullName)")
    }
}

"--- native format distribution ---"
$formatGroups.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { "$($_.Key): $($_.Value)" }
"--- non-Bgra8 files ---"
$oddities | ForEach-Object { $_ }
"--- explicit-conversion probe results ---"
$convertedResults | ForEach-Object { $_ }
