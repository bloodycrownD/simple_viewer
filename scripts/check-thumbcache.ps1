[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Drawing
$dir = "$env:LOCALAPPDATA\SimpleViewer\thumbcache"
$files = Get-ChildItem $dir -Filter '*.jpg' -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-15) } |
    Sort-Object LastWriteTime -Descending
"fresh thumbcache entries: $($files.Count)"
$bad = 0
foreach ($f in ($files | Select-Object -First 6)) {
    $bytes = [IO.File]::ReadAllBytes($f.FullName)
    $isJpeg = ($bytes[0] -eq 0xFF -and $bytes[1] -eq 0xD8)
    try {
        $img = [Drawing.Image]::FromStream([IO.MemoryStream]::new($bytes))
        $dim = "$($img.Width)x$($img.Height)"
        $img.Dispose()
    } catch {
        $dim = "INVALID: $($_.Exception.Message)"
        $bad++
    }
    "$($f.Name) jpeg=$isJpeg bytes=$($bytes.Length) dim=$dim"
}
if ($files.Count -eq 0) { "NO FRESH ENTRIES - walkthrough lib may reuse old cache" }
