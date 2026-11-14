[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PublishDirectory,
    [Parameter(Mandatory)] [string] $StorePublishDirectory,
    [Parameter(Mandatory)] [string] $ComplianceDirectory,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [Parameter(Mandatory)] [string] $MakeAppxPath,
    [Parameter(Mandatory)] [string] $WixPath,
    [Parameter(Mandatory)] [string] $WixBalExtensionPath,
    [Parameter(Mandatory)] [ValidatePattern('^[A-Fa-f0-9]{64}$')] [string] $ValidatedReceiverSha256,
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')] [string] $Version = '1.3.1.0',
    [ValidateSet('appx', 'msix')] [string] $StoreFormat = 'appx',
    [switch] $AlsoBuildMsix,
    [switch] $ValidateOnly,
    [switch] $PreflightFixture,
    [switch] $StoreOnly
)

$ErrorActionPreference = 'Stop'
$parsedVersion = [Version]::Parse($Version)
if ($parsedVersion.Major -lt 1 -or $parsedVersion.Major -gt 255 -or $parsedVersion.Minor -gt 255 -or
    $parsedVersion.Build -gt 65535 -or $parsedVersion.Revision -gt 0) {
    throw 'Release versions require major 1..255, minor 0..255, build 0..65535 and a zero Store revision; increment an MSI version field for an upgrade.'
}
$msiVersion = $parsedVersion.ToString(3)
$Version = $msiVersion + '.0'
if ($PreflightFixture -and -not $ValidateOnly) { throw 'Test-only preflight fixtures cannot produce release packages.' }
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$templates = Join-Path $repository 'packaging'
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
    $pending = [Collections.Generic.Stack[IO.FileSystemInfo]]::new()
    $pending.Push((Get-Item -LiteralPath $source -Force))
    while ($pending.Count -gt 0) {
        $entry = $pending.Pop()
        if ($entry.Name -in @('.clearcast-state', '.runtime', '.git')) {
            throw "Private/generated state in package input: $($entry.FullName)"
        }
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Reparse point in package input: $($entry.FullName)"
        }
        if ($entry.PSIsContainer) {
            foreach ($child in Get-ChildItem -LiteralPath $entry.FullName -Force) { $pending.Push($child) }
        }
    }
}

$files = 'ClearCast.exe', 'ClearCast.dll', 'Assets\ClearCast.png', 'Assets\ClearCast.ico',
    'LICENSE', 'PRIVACY.md', 'THIRD-PARTY-NOTICES.md', 'receiver\clearcast_receiver.dll',
    'receiver\gst-plugin-scanner.exe',
    'receiver\gstreamer-security-manifest.json', 'receiver\security-dependencies-manifest.json',
    'receiver\gstreamer-http-security-manifest.json', 'receiver\LICENSE-GStreamerHttpSecurity.txt'
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

foreach ($folder in @($publish, $storePublish)) {
    $dependencies = Get-Content -LiteralPath (Join-Path $folder 'receiver\security-dependencies-manifest.json') -Raw | ConvertFrom-Json
    if ($dependencies.schemaVersion -ne 1 -or $dependencies.components.Count -ne 2 -or
        @($dependencies.components | Where-Object { $_.name -ieq 'libplist' }).Count -ne 1 -or
        @($dependencies.components | Where-Object { $_.name -ieq 'openssl' }).Count -ne 1) {
        throw 'The controlled dependency manifest must identify libplist and OpenSSL exactly once.'
    }
    foreach ($component in $dependencies.components) {
        $name = $component.name.ToLowerInvariant()
        if (($name -eq 'libplist' -and ($component.version -ne '2.8.0' -or $component.files.Count -ne 1 -or
            $component.files[0].relativePath -ne 'libplist-2.0.dll')) -or
            ($name -eq 'openssl' -and ($component.version -ne '3.5.9' -or $component.files.Count -gt 2 -or
            @($component.files | Where-Object relativePath -eq 'libcrypto-3-x64.dll').Count -ne 1 -or
            @($component.files | Where-Object { $_.relativePath -notin @('libcrypto-3-x64.dll', 'libssl-3-x64.dll') }).Count -ne 0))) {
            throw "The controlled dependency version or file set does not match the release requirements: $name"
        }
        if ($component.version -notmatch '^\d+\.\d+\.\d+$' -or $component.sourceSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
            throw 'Invalid controlled dependency version or source digest.'
        }
        $noticePath = Join-Path $compliance "licenses\$name-$($component.version)\source-build.json"
        if (-not (Test-Path -LiteralPath $noticePath -PathType Leaf)) { throw "Missing controlled dependency notice: $name" }
        $dependencyNotice = Get-Content -LiteralPath $noticePath -Raw | ConvertFrom-Json
        if ($dependencyNotice.version -ne $component.version -or $dependencyNotice.sourceSha256 -ne $component.sourceSha256 -or
            $dependencyNotice.sourceUrl -ne $component.sourceUrl -or $dependencyNotice.files.Count -ne $component.files.Count -or
            $component.files.Count -lt 1) { throw "Controlled dependency source/build notice mismatch: $name" }
        foreach ($file in $component.files) {
            if ($file.relativePath -notin @('libplist-2.0.dll', 'libcrypto-3-x64.dll', 'libssl-3-x64.dll') -or
                $file.sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw "Unexpected controlled dependency file: $name" }
            $noticeFile = @($dependencyNotice.files | Where-Object relativePath -eq $file.relativePath)
            if ($noticeFile.Count -ne 1 -or $noticeFile[0].sha256 -ne $file.sha256 -or
                (Get-FileHash -LiteralPath (Join-Path $folder "receiver\$($file.relativePath)")).Hash -ne $file.sha256) {
                throw "Controlled dependency file or notice hash does not match: $($file.relativePath)"
            }
        }
    }
    $assembly = Join-Path $folder 'ClearCast.dll'
    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($assembly)
    if ($versionInfo.ProductVersion -match '\+ui-tests(?:\.|$)') {
        throw 'A UI-test application cannot be packaged for release. Publish without EnableUiTests.'
    }
    $fileVersion = $versionInfo.FileVersion
    if ($fileVersion -ne $Version) {
        throw "The published application version $fileVersion does not match $Version."
    }
    if ((Get-FileHash -LiteralPath (Join-Path $folder 'receiver\clearcast_receiver.dll')).Hash -ne $ValidatedReceiverSha256) {
        throw 'The published receiver does not match the independently validated release candidate.'
    }
    foreach ($index in 0..199) {
        $frame = Join-Path $folder ('Assets\MirroringAnimation\frame-{0:D3}.svg' -f $index)
        if (-not (Test-Path -LiteralPath $frame -PathType Leaf)) { throw "Missing animation frame: $frame" }
    }
    if (@(Get-ChildItem -LiteralPath (Join-Path $folder 'Assets\MirroringAnimation') -Filter '*.svg' -File).Count -ne 200) {
        throw 'The published animation must contain exactly 200 SVG frames.'
    }
}
foreach ($relative in @('receiver\gst-plugin-scanner.exe', 'receiver\clearcast_receiver.dll')) {
    if ((Get-FileHash -LiteralPath (Join-Path $publish $relative)).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $storePublish $relative)).Hash) {
        throw "Desktop and Store receiver runtime files do not match: $relative"
    }
}

$parserNotice = Join-Path $compliance 'licenses\gst-plugins-bad-1.28.9\source-build.json'
if (-not (Test-Path -LiteralPath $parserNotice)) {
    throw 'The distribution notices must include the exact rebuilt GStreamer parser source-build.json.'
}
$noticeManifest = Get-Content -LiteralPath $parserNotice -Raw | ConvertFrom-Json
$parserPaths = @('libgstcodecparsers-1.0-0.dll', 'lib/gstreamer-1.0/libgstvideoparsersbad.dll', 'libgstadaptivedemux-1.0-0.dll')
$sourceHash = '4A4E0B7BF84F868378C48F143A7AC3A98923B488A5B74F1A35BA46BC3C43F6B1'
$parserPatchRelative = 'src/native/patches/gstreamer-adaptive-demux-time.patch'
$parserPatchHash = (Get-FileHash -LiteralPath (Join-Path $repository $parserPatchRelative)).Hash
$parserNoticeDirectory = Split-Path -Parent $parserNotice
if (-not (Test-Path -LiteralPath (Join-Path $parserNoticeDirectory 'COPYING') -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $parserNoticeDirectory 'gstreamer-adaptive-demux-time.patch') -PathType Leaf) -or
    (Get-FileHash -LiteralPath (Join-Path $parserNoticeDirectory 'gstreamer-adaptive-demux-time.patch')).Hash -ne $parserPatchHash) {
    throw 'The parser/adaptive distribution license or controlled patch is missing or changed.'
}
foreach ($folder in @($publish, $storePublish)) {
    $receiver = Join-Path $folder 'receiver'
    $manifest = Get-Content -LiteralPath (Join-Path $receiver 'gstreamer-security-manifest.json') -Raw | ConvertFrom-Json
    foreach ($metadata in @($manifest, $noticeManifest)) {
        if ($metadata.schemaVersion -ne 1 -or $metadata.version -ne '1.28.9' -or
            $metadata.sourceSha256 -ne $sourceHash -or $metadata.files.Count -ne 3 -or
            $metadata.sourceUrl -ne 'https://gstreamer.freedesktop.org/src/gst-plugins-bad/gst-plugins-bad-1.28.9.tar.xz' -or
            $metadata.sourcePatch.relativePath -ne $parserPatchRelative -or $metadata.sourcePatch.sha256 -ne $parserPatchHash -or
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
    if ((Get-FileHash -LiteralPath (Join-Path $receiver 'LICENSE-GStreamerSecurity.txt')).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $parserNoticeDirectory 'COPYING')).Hash) {
        throw 'The parser/adaptive runtime and distribution license differ.'
    }
}
$httpNoticeDirectory = Join-Path $compliance 'licenses\gst-plugins-good-1.28.9'
$httpNoticePath = Join-Path $httpNoticeDirectory 'source-build.json'
$httpPatchRelative = 'src/native/patches/gstreamer-soup-destination.patch'
$httpSourceHash = 'E518CD97483706A252E95E60312A22D23DEC6F5E7673AF6F5BDE719C3A9CA7F4'
$httpSourceUrl = 'https://gstreamer.freedesktop.org/src/gst-plugins-good/gst-plugins-good-1.28.9.tar.xz'
$httpPatchHash = (Get-FileHash -LiteralPath (Join-Path $repository $httpPatchRelative)).Hash
foreach ($required in @($httpNoticePath, (Join-Path $httpNoticeDirectory 'COPYING'),
    (Join-Path $httpNoticeDirectory 'gstreamer-soup-destination.patch'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw 'Missing controlled HTTP source distribution notice.' }
}
$httpNotice = Get-Content -LiteralPath $httpNoticePath -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath (Join-Path $httpNoticeDirectory 'gstreamer-soup-destination.patch')).Hash -ne $httpPatchHash) {
    throw 'The HTTP source distribution patch hash does not match the controlled source patch.'
}
foreach ($folder in @($publish, $storePublish)) {
    $receiver = Join-Path $folder 'receiver'
    $httpManifest = Get-Content -LiteralPath (Join-Path $receiver 'gstreamer-http-security-manifest.json') -Raw | ConvertFrom-Json
    foreach ($metadata in @($httpManifest, $httpNotice)) {
        if ($metadata.schemaVersion -ne 1 -or $metadata.version -ne '1.28.9' -or
            $metadata.sourceSha256 -ne $httpSourceHash -or $metadata.sourceUrl -cne $httpSourceUrl -or
            @($metadata.files).Count -ne 1 -or $metadata.files[0].relativePath -cne 'lib/gstreamer-1.0/libgstsoup.dll' -or
            $metadata.files[0].sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
            $metadata.sourcePatch.relativePath -cne $httpPatchRelative -or $metadata.sourcePatch.sha256 -ne $httpPatchHash) {
            throw 'The HTTP source manifest or notice does not match the pinned source, file and controlled patch.'
        }
    }
    if ($httpManifest.files[0].sha256 -ne $httpNotice.files[0].sha256 -or
        (Get-FileHash -LiteralPath (Join-Path $receiver $httpManifest.files[0].relativePath)).Hash -ne $httpManifest.files[0].sha256) {
        throw 'The controlled HTTP source binary or notice hash does not match.'
    }
    if ((Get-FileHash -LiteralPath (Join-Path $receiver 'LICENSE-GStreamerHttpSecurity.txt')).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $httpNoticeDirectory 'COPYING')).Hash) {
        throw 'The controlled HTTP source license does not match its distribution notice.'
    }
}
if (-not $PreflightFixture) {
    $inventoryPath = Join-Path $compliance 'runtime-components.json'
    $coveragePath = Join-Path $compliance 'source-coverage.json'
    if (-not (Test-Path -LiteralPath $inventoryPath -PathType Leaf) -or -not (Test-Path -LiteralPath $coveragePath -PathType Leaf) -or
        [IO.File]::ReadAllText((Join-Path $compliance 'THIRD-PARTY-COMPONENTS.md')) -match 'TEST-ONLY') {
        throw 'Release packages require the complete exact runtime/source inventory; test-only or incomplete notices cannot be shipped.'
    }
    $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
    if ($inventory.schemaVersion -ne 1 -or $inventory.version -ne $Version) { throw 'The complete runtime inventory does not match the release.' }
    $coverage = Get-Content -LiteralPath $coveragePath -Raw | ConvertFrom-Json
    $packageGroups = @($inventory.files | Where-Object package | Group-Object package)
    $peCount = @($inventory.files | Where-Object { [IO.Path]::GetExtension($_.relativePath) -in @('.dll', '.exe') }).Count
    if ($coverage.inventorySha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
        $coverage.inventorySha256 -ne (Get-FileHash -LiteralPath (Join-Path $compliance 'THIRD-PARTY-COMPONENTS.md')).Hash -or
        $coverage.fileCount -ne @($inventory.files).Count -or $coverage.peFileCount -ne $peCount -or
        $coverage.msysPackageCount -ne $packageGroups.Count -or $coverage.baselineSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
        throw 'Source coverage does not match the inventory text, exact file counts, package count or baseline digest.'
    }
    foreach ($source in @($coverage.additionalSources.PSObject.Properties)) {
        $record = $source.Value
        $msysSource = $source.Name -cmatch '^msys2/mingw-w64-[A-Za-z0-9+_.~-]+\.src\.tar\.zst$' -and
            $record.pkgbuildSha256 -match '^[A-Fa-f0-9]{64}$'
        $httpSource = $source.Name -ceq 'gstreamer/gst-plugins-good-1.28.9.tar.xz' -and $record.sha256 -eq $httpSourceHash
        $parserSource = $source.Name -ceq 'gstreamer/gst-plugins-bad-1.28.9.tar.xz' -and $record.sha256 -eq $sourceHash
        if ((-not $msysSource -and -not $httpSource -and -not $parserSource) -or
            $record.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
            [IO.Path]::GetFileName($record.path) -ne [IO.Path]::GetFileName($source.Name) -or
            -not (Test-Path -LiteralPath $record.path -PathType Leaf) -or (Get-FileHash -LiteralPath $record.path).Hash -ne $record.sha256) {
            throw 'An exact additional source archive is missing or does not match source coverage.'
        }
    }
    if ($coverage.httpSource.sha256 -ne $httpSourceHash -or
        $coverage.httpSource.member -cne 'gstreamer/gst-plugins-good-1.28.9.tar.xz' -or
        ($coverage.httpSource.location -notin @('baseline', 'additional'))) {
        throw 'Source coverage does not bind the controlled HTTP source archive.'
    }
    if ($coverage.httpSource.location -eq 'additional' -and
        -not $coverage.additionalSources.'gstreamer/gst-plugins-good-1.28.9.tar.xz') {
        throw 'The controlled HTTP corresponding source archive is missing from source coverage.'
    }
    if ($coverage.parserSource.member -cne 'gstreamer/gst-plugins-bad-1.28.9.tar.xz' -or
        $coverage.parserSource.sha256 -ne $sourceHash -or $coverage.parserSource.location -notin @('baseline','additional') -or
        ($coverage.parserSource.location -eq 'additional' -and -not $coverage.additionalSources.'gstreamer/gst-plugins-bad-1.28.9.tar.xz')) {
        throw 'Source coverage does not bind the controlled parser/adaptive source archive.'
    }
    foreach ($group in $packageGroups) {
        if ($group.Name -notmatch '^mingw-w64-ucrt-x86_64-[A-Za-z0-9+_.-]+$') { throw 'Invalid runtime package identity.' }
        $directory = Join-Path $compliance ('licenses/' + $group.Name)
        $notice = Get-Content -LiteralPath (Join-Path $directory 'source-build.json') -Raw | ConvertFrom-Json
        if ($notice.package -ne $group.Name -or $notice.version -ne $group.Group[0].version -or
            $notice.sourceSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or @($notice.files).Count -ne $group.Count -or
            @(Get-ChildItem -LiteralPath $directory -Recurse -File | Where-Object Name -ne 'source-build.json').Count -lt 1) {
            throw 'A runtime package lacks its matching source/build record or distribution license.'
        }
        foreach ($file in $group.Group) {
            $matched = @($notice.files | Where-Object relativePath -eq $file.relativePath)
            if ($matched.Count -ne 1 -or $matched[0].sha256 -ne $file.sha256) { throw 'A runtime package source record does not match its shipped files.' }
        }
    }
    foreach ($folder in @($publish, $storePublish)) {
        $receiverRoot = Join-Path $folder 'receiver'
        $actualFiles = @(Get-ChildItem -LiteralPath $receiverRoot -Recurse -File | Where-Object {
            $_.Extension -in @('.dll', '.exe') -or $_.Name -eq 'ca-bundle.crt'
        } | ForEach-Object { [IO.Path]::GetRelativePath($receiverRoot, $_.FullName).Replace('\', '/') })
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($file in $inventory.files) {
            $relative = [string]$file.relativePath
            if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.([\\/]|$)' -or -not $names.Add($relative.Replace('\', '/')) -or
                $file.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
                (Get-FileHash -LiteralPath (Join-Path $receiverRoot $relative)).Hash -ne $file.sha256) {
                throw "The complete runtime inventory has an invalid or mismatched file: $relative"
            }
        }
        if (@(Compare-Object $actualFiles @($names)).Count) { throw 'The complete runtime inventory does not cover the exact PE and certificate closure.' }
    }
}
if ($ValidateOnly) {
    [pscustomobject]@{ Validated = $true; Version = $Version; MsiVersion = $msiVersion; Desktop = $publish; Store = $storePublish; Notices = $compliance }
    return
}

[void] [IO.Directory]::CreateDirectory($output)
$stage = Join-Path $output ('.packaging-' + [Guid]::NewGuid().ToString('N'))
[void] [IO.Directory]::CreateDirectory($stage)
$baseName = "ClearCast-$Version-Windows-x64"
$setupName = "$baseName-Setup.exe"
$storeName = "$baseName-Store.$StoreFormat"

function Copy-Payload([string] $Source, [string] $Destination) {
    [void] [IO.Directory]::CreateDirectory($Destination)
    Get-ChildItem -LiteralPath $Source -Force | Where-Object Name -NotIn @('ClearCast.pdb','licenses','THIRD-PARTY-COMPONENTS.md','runtime-components.json','source-coverage.json') |
        Copy-Item -Destination $Destination -Recurse
    Get-ChildItem -LiteralPath $compliance -Force | Copy-Item -Destination $Destination -Recurse -Force
}

try {
    if (-not $StoreOnly) {
    $payload = Join-Path $stage 'installer'
    Copy-Payload $publish $payload
    $longPaths = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object { $_.FullName.Length -ge 260 })
    if ($longPaths.Count) {
        throw "WiX's native cabinet helper requires source paths shorter than 260 characters. Shorten the output path or notice layout: $($longPaths[0].FullName) ($($longPaths[0].FullName.Length) characters)."
    }
    $msi = Join-Path $stage 'ClearCast.msi'
    $setup = Join-Path $stage $setupName
    $wixOptions = @('-arch', 'x64', '-d', "ReleaseVersion=$Version", '-d', "MsiVersion=$msiVersion", '-bindpath', "Payload=$payload",
        '-intermediateFolder', (Join-Path $stage 'wix-obj'), '-pdbtype', 'none')
    & $wix build (Join-Path $templates 'ClearCast.wxs') @wixOptions -o $msi
    if ($LASTEXITCODE -ne 0) { throw 'WiX failed to build the MSI.' }
    & $wix build (Join-Path $templates 'Bundle.wxs') @wixOptions -ext $balExtension -d "MsiPath=$msi" -o $setup
    if ($LASTEXITCODE -ne 0) { throw 'WiX failed to build the EXE installer.' }
    }

    $store = Join-Path $stage 'store'
    Copy-Payload $storePublish $store
    $manifest = [xml] [IO.File]::ReadAllText((Join-Path $templates 'AppxManifest.xml'))
    $manifest.Package.Identity.Version = $Version
    $manifest.Save((Join-Path $store 'AppxManifest.xml'))
    # Qualified Shell assets are project content and already indexed by the
    # Store resources.pri. Do not add logos after that index was generated.
    foreach ($logo in 'StoreLogo.png', 'Square44x44Logo.png', 'Square150x150Logo.png',
        'StoreLogo.scale-200.png', 'StoreLogo.scale-400.png',
        'Square44x44Logo.scale-200.png', 'Square44x44Logo.scale-400.png',
        'Square150x150Logo.scale-200.png', 'Square150x150Logo.scale-400.png') {
        if (-not (Test-Path -LiteralPath (Join-Path $store "Assets\$logo") -PathType Leaf)) {
            throw "Missing indexed Shell icon asset: $logo. Regenerate app icons and republish the Store app."
        }
    }
    foreach ($size in 16,20,24,30,32,36,40,48,60,64,72,80,96,256) {
        foreach ($suffix in '', '_altform-unplated', '_altform-lightunplated') {
            $logo = "Square44x44Logo.targetsize-$size$suffix.png"
            if (-not (Test-Path -LiteralPath (Join-Path $store "Assets\$logo") -PathType Leaf)) {
                throw "Missing indexed Shell icon asset: $logo. Regenerate app icons and republish the Store app."
            }
        }
    }
    $storePackage = Join-Path $stage $storeName
    $makeAppxLog = Join-Path $stage 'makeappx.log'
    & $makeAppx pack /d $store /p $storePackage /no *> $makeAppxLog
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $makeAppxLog -Tail 15 | Out-Host
        throw 'MakeAppx failed to build the Store MSIX.'
    }
    if ($AlsoBuildMsix -and $StoreFormat -ne 'msix') {
        $msixName = "$baseName-Store.msix"
        $msixPackage = Join-Path $stage $msixName
        & $makeAppx pack /d $store /p $msixPackage /no *> (Join-Path $stage 'makeappx-msix.log')
        if ($LASTEXITCODE -ne 0) { throw 'MakeAppx failed to build the comparison Store MSIX.' }
        [IO.File]::Move($msixPackage, (Join-Path $output $msixName), $true)
    }

    if (-not $StoreOnly) { [IO.File]::Move($setup, (Join-Path $output $setupName), $true) }
    [IO.File]::Move($storePackage, (Join-Path $output $storeName), $true)
    if (-not $StoreOnly) { Get-Item -LiteralPath (Join-Path $output $setupName) }
    Get-Item -LiteralPath (Join-Path $output $storeName)
} finally {
    $resolved = (Resolve-Path -LiteralPath $stage).Path
    $outputPrefix = (Resolve-Path -LiteralPath $output).Path.TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean staging outside $output."
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
