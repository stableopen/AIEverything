$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$output = [System.IO.Path]::GetFullPath(
    (Join-Path $root 'src\AIEverything.App\Assets\AIEverything.ico'))
$rootPrefix = $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to write icon outside repository root: $output"
}

function New-RoundedRectanglePath([float] $x, [float] $y, [float] $width, [float] $height, [float] $radius) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $radius * 2
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc($x + $width - $diameter, $y, $diameter, $diameter, 270, 90)
    $path.AddArc($x + $width - $diameter, $y + $height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($x, $y + $height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconDib([int] $size) {
    $bitmap = [System.Drawing.Bitmap]::new(
        $size,
        $size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

        $inset = [math]::Max(1, $size * 0.025)
        $path = New-RoundedRectanglePath `
            $inset $inset ($size - 2 * $inset) ($size - 2 * $inset) ($size * 0.21)
        $brandBrush = [System.Drawing.SolidBrush]::new(
            [System.Drawing.Color]::FromArgb(255, 15, 108, 189))
        try {
            $graphics.FillPath($brandBrush, $path)
        }
        finally {
            $brandBrush.Dispose()
            $path.Dispose()
        }

        $penWidth = [math]::Max(1.35, $size * 0.075)
        $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, $penWidth)
        try {
            $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
            $graphics.DrawEllipse(
                $pen,
                $size * 0.235,
                $size * 0.195,
                $size * 0.43,
                $size * 0.43)
            $graphics.DrawLine(
                $pen,
                $size * 0.59,
                $size * 0.57,
                $size * 0.79,
                $size * 0.77)
        }
        finally {
            $pen.Dispose()
        }

        $stream = [System.IO.MemoryStream]::new()
        $writer = [System.IO.BinaryWriter]::new($stream)
        try {
            $maskRowBytes = [int]([math]::Ceiling($size / 32.0) * 4)
            $pixelBytes = $size * $size * 4
            $writer.Write([uint32]40)
            $writer.Write([int32]$size)
            $writer.Write([int32]($size * 2))
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]0)
            $writer.Write([uint32]$pixelBytes)
            $writer.Write([int32]0)
            $writer.Write([int32]0)
            $writer.Write([uint32]0)
            $writer.Write([uint32]0)

            for ($y = $size - 1; $y -ge 0; $y--) {
                for ($x = 0; $x -lt $size; $x++) {
                    $color = $bitmap.GetPixel($x, $y)
                    $writer.Write([byte]$color.B)
                    $writer.Write([byte]$color.G)
                    $writer.Write([byte]$color.R)
                    $writer.Write([byte]$color.A)
                }
            }

            $writer.Write([byte[]]::new($maskRowBytes * $size))
            Write-Output -NoEnumerate $stream.ToArray()
        }
        finally {
            $writer.Dispose()
            $stream.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$sizes = @(16, 32, 48, 256)
$images = @($sizes | ForEach-Object { New-IconDib $_ })
$directory = Split-Path -Parent $output
[System.IO.Directory]::CreateDirectory($directory) | Out-Null

$stream = [System.IO.File]::Open(
    $output,
    [System.IO.FileMode]::Create,
    [System.IO.FileAccess]::Write,
    [System.IO.FileShare]::None)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $size = $sizes[$index]
        $image = $images[$index]
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Length
    }

    foreach ($image in $images) {
        $writer.Write($image)
    }
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}

$validationIcon = [System.Drawing.Icon]::new($output, 32, 32)
try {
    if ($validationIcon.Width -ne 32 -or $validationIcon.Height -ne 32) {
        throw "Generated icon did not expose the expected 32x32 image."
    }
}
finally {
    $validationIcon.Dispose()
}

Write-Output "PASS: generated $output"
