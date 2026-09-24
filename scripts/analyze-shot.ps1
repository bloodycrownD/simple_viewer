# Pixel-analyze the screenshot: what's in the window?
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile("$env:TEMP\sv-direct.png")
"size: $($bmp.Width)x$($bmp.Height)"
$colors = @{}
for ($y = 0; $y -lt $bmp.Height; $y += 12) {
  for ($x = 0; $x -lt $bmp.Width; $x += 12) {
    $c = $bmp.GetPixel($x, $y)
    $k = "$($c.R),$($c.G),$($c.B)"
    if (-not $colors.ContainsKey($k)) { $colors[$k] = 0 }
    $colors[$k]++
  }
}
"distinct colors: $($colors.Count)"
"top 8:"
$colors.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 8 | ForEach-Object { "  $($_.Key) = $($_.Value)" }
# horizontal band profile at several rows
"row profile (avg RGB per 100px band):"
foreach ($y in @(60, 150, 300, 450, 600, 750, 850)) {
  $row = @()
  for ($x = 50; $x -lt 1600; $x += 200) {
    $c = $bmp.GetPixel($x, $y)
    $row += "$($c.R),$($c.G),$($c.B)"
  }
  "y=$y : " + ($row -join ' | ')
}
$bmp.Dispose()
