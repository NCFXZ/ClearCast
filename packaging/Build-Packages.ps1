[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PublishDirectory,
    [Parameter(Mandatory)] [string] $StorePublishDirectory,
    [Parameter(Mandatory)] [string] $ComplianceDirectory,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [Parameter(Mandatory)] [string] $MakeAppxPath,
    [Parameter(Mandatory)] [string] $WixPath,
    [Parameter(Mandatory)] [string] $WixBalExtensionPath,
    [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version = '1.0.0',
    [switch] $ValidateOnly
)

$ErrorActionPreference = 'Stop'
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$storePublish = (Resolve-Path -LiteralPath $StorePublishDirectory).Path
$compliance = (Resolve-Path -LiteralPath $ComplianceDirectory).Path
$makeAppx = (Resolve-Path -LiteralPath $MakeAppxPath).Path
$wix = (Resolve-Path -LiteralPath $WixPath).Path
$balExtension = (Resolve-Path -LiteralPath $WixBalExtensionPath).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)

foreach ($source in @($publish, $storePublish, $compliance)) {
    $sourcePrefix = [IO.Path]::TrimEndingDirectorySeparator($source) + [IO.Path]::DirectorySeparatorChar
    if ($output -eq $source -or $output.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The output must be outside the input directory $source."
    }
}

$files = 'ClearCast.exe', 'ClearCast.dll', 'Assets\ClearCast.png', 'Assets\ClearCast.ico',
    'LICENSE', 'PRIVACY.md', 'THIRD-PARTY-NOTICES.md', 'receiver\clearcast_receiver.dll',
    'receiver\gstreamer-security-manifest.json'
foreach ($inputFiles in @(
    @{ Folder = $publish; Files = $files + 'ClearCast.pri' },
    @{ Folder = $storePublish; Files = $files + 'resources.pri' },
    @{ Folder = $compliance; Files = 'THIRD-PARTY-COMPONENTS.md', 'licenses' }
)) {
    foreach ($file in $inputFiles.Files) {
        if (-not (Test-Path -LiteralPath (Join-Path $inputFiles.Folder $file))) {
            throw "Missing $file in $($inputFiles.Folder)."
        }
    }
}

$parserNotice = Join-Path $compliance 'licenses\gst-plugins-bad-1.28.9\source-build.json'
if (-not (Test-Path -LiteralPath $parserNotice)) {
    throw 'The distribution notices must include the exact rebuilt GStreamer parser source-build.json.'
}
$noticeManifest = Get-Content -LiteralPath $parserNotice -Raw | ConvertFrom-Json
$parserPaths = @('libgstcodecparsers-1.0-0.dll', 'lib/gstreamer-1.0/libgstvideoparsersbad.dll')
$sourceHash = '4A4E0B7BF84F868378C48F143A7AC3A98923B488A5B74F1A35BA46BC3C43F6B1'
foreach ($folder in @($publish, $storePublish)) {
    $receiver = Join-Path $folder 'receiver'
    $manifest = Get-Content -LiteralPath (Join-Path $receiver 'gstreamer-security-manifest.json') -Raw | ConvertFrom-Json
    foreach ($metadata in @($manifest, $noticeManifest)) {
        if ($metadata.schemaVersion -ne 1 -or $metadata.version -ne '1.28.9' -or
            $metadata.sourceSha256 -ne $sourceHash -or $metadata.files.Count -ne 2 -or
            @(Compare-Object $parserPaths @($metadata.files.relativePath)).Count -ne 0) {
            throw 'The package or notice parser manifest does not match the pinned GStreamer 1.28.9 source/components.'
        }
    }
    foreach ($file in $manifest.files) {
        $path = Join-Path $receiver $file.relativePath
        $noticeFile = @($noticeManifest.files | Where-Object relativePath -eq $file.relativePath)
        if ($noticeFile.Count -ne 1 -or $file.sha256 -ne $noticeFile[0].sha256 -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) {
            throw "The published parser file or distribution notice hash does not match: $path"
        }
    }
}
if ($ValidateOnly) {
    [pscustomobject]@{ Validated = $true; Desktop = $publish; Store = $storePublish; Notices = $compliance }
    return
}

[void] [IO.Directory]::CreateDirectory($output)
$stage = Join-Path $output ('.packaging-' + [Guid]::NewGuid().ToString('N'))
[void] [IO.Directory]::CreateDirectory($stage)
$baseName = "ClearCast-$Version-Windows-x64"
$setupName = "$baseName-Setup.exe"
$storeName = "$baseName-Store.msix"

function Copy-Payload([string] $Source, [string] $Destination) {
    [void] [IO.Directory]::CreateDirectory($Destination)
    Get-ChildItem -LiteralPath $Source -Force | Where-Object Name -ne 'ClearCast.pdb' |
        Copy-Item -Destination $Destination -Recurse
    Get-ChildItem -LiteralPath $compliance -Force | Copy-Item -Destination $Destination -Recurse
}

try {
    $payload = Join-Path $stage 'installer'
    Copy-Payload $publish $payload
    $msi = Join-Path $stage 'ClearCast.msi'
    $setup = Join-Path $stage $setupName
    $wixOptions = @('-arch', 'x64', '-d', "ReleaseVersion=$Version", '-bindpath', "Payload=$payload",
        '-intermediateFolder', (Join-Path $stage 'wix-obj'), '-pdbtype', 'none')
    & $wix build (Join-Path $PSScriptRoot 'ClearCast.wxs') @wixOptions -o $msi
    if ($LASTEXITCODE -ne 0) { throw 'WiX failed to build the MSI.' }
    & $wix build (Join-Path $PSScriptRoot 'Bundle.wxs') @wixOptions -ext $balExtension -d "MsiPath=$msi" -o $setup
    if ($LASTEXITCODE -ne 0) { throw 'WiX failed to build the EXE installer.' }

    $store = Join-Path $stage 'store'
    Copy-Payload $storePublish $store
    $manifest = [xml] [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'AppxManifest.xml'))
    $manifest.Package.Identity.Version = "$Version.0"
    $manifest.Save((Join-Path $store 'AppxManifest.xml'))
    Add-Type -AssemblyName System.Drawing
    $image = [Drawing.Image]::FromFile((Join-Path $store 'Assets\ClearCast.png'))
    try {
        foreach ($logo in @(
            @{ Name = 'StoreLogo.png'; Size = 50 },
            @{ Name = 'Square44x44Logo.png'; Size = 44 },
            @{ Name = 'Square150x150Logo.png'; Size = 150 }
        )) {
            $bitmap = [Drawing.Bitmap]::new($logo.Size, $logo.Size)
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.DrawImage($image, 0, 0, $logo.Size, $logo.Size)
                $bitmap.Save((Join-Path $store "Assets\$($logo.Name)"), [Drawing.Imaging.ImageFormat]::Png)
            } finally {
                $graphics.Dispose()
                $bitmap.Dispose()
            }
        }
    } finally {
        $image.Dispose()
    }
    $storePackage = Join-Path $stage $storeName
    $makeAppxLog = Join-Path $stage 'makeappx.log'
    & $makeAppx pack /d $store /p $storePackage /no *> $makeAppxLog
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $makeAppxLog -Tail 15 | Out-Host
        throw 'MakeAppx failed to build the Store MSIX.'
    }

    [IO.File]::Move($setup, (Join-Path $output $setupName), $true)
    [IO.File]::Move($storePackage, (Join-Path $output $storeName), $true)
    Get-Item -LiteralPath (Join-Path $output $setupName), (Join-Path $output $storeName)
} finally {
    $resolved = (Resolve-Path -LiteralPath $stage).Path
    $outputPrefix = (Resolve-Path -LiteralPath $output).Path.TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean staging outside $output."
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
