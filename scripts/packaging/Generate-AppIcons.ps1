[CmdletBinding()]
param(
    [string] $SourcePath,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $SourcePath) { $SourcePath = Join-Path $repository 'src\ClearCast\Assets\ClearCast.png' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repository 'src\ClearCast\Assets' }
$source = (Resolve-Path -LiteralPath $SourcePath).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
[void] [IO.Directory]::CreateDirectory($output)

# Shell selects qualified package resources, independently of the window HICON.
# Keep the existing logo design and transparent corners at each exact DPI size.
# https://learn.microsoft.com/windows/apps/design/iconography/app-icon-construction
$assets = @(
    @{ Name = 'StoreLogo.png'; Size = 50 },
    @{ Name = 'StoreLogo.scale-200.png'; Size = 100 },
    @{ Name = 'StoreLogo.scale-400.png'; Size = 200 },
    @{ Name = 'Square44x44Logo.png'; Size = 44 },
    @{ Name = 'Square44x44Logo.scale-200.png'; Size = 88 },
    @{ Name = 'Square44x44Logo.scale-400.png'; Size = 176 },
    @{ Name = 'Square150x150Logo.png'; Size = 150 },
    @{ Name = 'Square150x150Logo.scale-200.png'; Size = 300 },
    @{ Name = 'Square150x150Logo.scale-400.png'; Size = 600 }
)
foreach ($size in 16,20,24,30,32,36,40,48,60,64,72,80,96,256) {
    foreach ($suffix in '', '_altform-unplated', '_altform-lightunplated') {
        $assets += @{ Name = "Square44x44Logo.targetsize-$size$suffix.png"; Size = $size }
    }
}

Add-Type -AssemblyName System.Drawing
$image = [Drawing.Image]::FromFile($source)
try {
    if ($image.Width -ne $image.Height) { throw 'The authoritative logo must be square.' }
    foreach ($asset in $assets) {
        $bitmap = [Drawing.Bitmap]::new($asset.Size, $asset.Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $attributes = [Drawing.Imaging.ImageAttributes]::new()
        try {
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
            $graphics.DrawImage($image, [Drawing.Rectangle]::new(0, 0, $asset.Size, $asset.Size),
                0, 0, $image.Width, $image.Height, [Drawing.GraphicsUnit]::Pixel, $attributes)
            $bitmap.Save((Join-Path $output $asset.Name), [Drawing.Imaging.ImageFormat]::Png)
        } finally {
            $attributes.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
        }
    }
} finally { $image.Dispose() }
Get-ChildItem -LiteralPath $output -File | Where-Object Name -In $assets.Name
