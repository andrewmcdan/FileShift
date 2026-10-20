$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repo = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repo 'logo.png'
$assets = Join-Path $repo 'assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null

# The supplied logo has generous outer padding. Keep the source unchanged and
# frame the central mark for small Windows icons and in-app branding tiles.
$image = [System.Drawing.Image]::FromFile($source)
try {
    $side = [int]([Math]::Min($image.Width, $image.Height) * 0.625)
    $crop = [System.Drawing.Rectangle]::new(
        [int](($image.Width - $side) / 2),
        [int](($image.Height - $side) / 2), $side, $side)
    $sizes = @(256, 128, 64, 48, 32, 24, 16)
    $frames = @()
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.DrawImage($image, [System.Drawing.Rectangle]::new(0, 0, $size, $size), $crop, [System.Drawing.GraphicsUnit]::Pixel)
            } finally { $graphics.Dispose() }
            $png = [System.IO.MemoryStream]::new()
            try {
                $bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
                $frames += ,$png.ToArray()
                if ($size -eq 256) { [System.IO.File]::WriteAllBytes((Join-Path $assets 'FileShift.Mark.png'), $png.ToArray()) }
            } finally { $png.Dispose() }
        } finally { $bitmap.Dispose() }
    }

    # ICO directory with PNG-encoded frames, supported by Windows 10 and later.
    $stream = [System.IO.File]::Create((Join-Path $assets 'FileShift.ico'))
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($frameIndex = 0; $frameIndex -lt $sizes.Count; $frameIndex++) {
            $dimension = if ($sizes[$frameIndex] -eq 256) { 0 } else { $sizes[$frameIndex] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$frameIndex].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$frameIndex].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    } finally { $writer.Dispose(); $stream.Dispose() }
} finally { $image.Dispose() }
Write-Host 'Updated FileShift branding from logo.png (16–256 pixel icon frames).'
