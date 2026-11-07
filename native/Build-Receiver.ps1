[CmdletBinding()]
param(
    [string] $ArchivePath,
    [string] $Ucrt64Directory,
    [string] $BuildDirectory,
    [string] $OutputDirectory,
    [string] $SecurityRuntimeDirectory
)

$ErrorActionPreference = 'Stop'
$runtime = Join-Path (Split-Path -Parent $PSScriptRoot) '.runtime'
$commit = 'd4b7373cb6107b2d8d6c5838991c19d39e2b96ae'
$expectedArchiveHash = 'D85E077EC7B20D487A2F68BDE7FEBCD1C9941D0F6C83FE740BB10BE3268F47BD'
if (-not $ArchivePath) { $ArchivePath = Join-Path $runtime 'uxplay-d4b7373.tar.gz' }
if (-not $Ucrt64Directory) { $Ucrt64Directory = Join-Path $runtime 'msys64\ucrt64' }
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $runtime 'uxplay-receiver-build' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $runtime 'receiver' }
if (-not $SecurityRuntimeDirectory) { $SecurityRuntimeDirectory = Join-Path $runtime 'gstreamer-security' }

# UCRT64 currently supplies GStreamer 1.28.8. Ship the ABI-compatible 1.28.9
# parser components to include the H.264/H.265/MPEG parser security fixes.
$securityRuntime = [IO.Path]::GetFullPath($SecurityRuntimeDirectory)
$securityManifestPath = Join-Path $securityRuntime 'manifest.json'
if (-not (Test-Path -LiteralPath $securityManifestPath)) {
    throw 'Build the pinned parser security components with native\Build-GStreamerSecurity.ps1 first.'
}
$securityManifest = Get-Content -LiteralPath $securityManifestPath -Raw | ConvertFrom-Json
if ($securityManifest.schemaVersion -ne 1 -or $securityManifest.version -ne '1.28.9' -or
    $securityManifest.sourceSha256 -ne '4A4E0B7BF84F868378C48F143A7AC3A98923B488A5B74F1A35BA46BC3C43F6B1') {
    throw 'The parser security component manifest is incompatible.'
}
$securityOverrides = @{}
foreach ($relativePath in 'libgstcodecparsers-1.0-0.dll', 'lib/gstreamer-1.0/libgstvideoparsersbad.dll') {
    $records = @($securityManifest.files | Where-Object relativePath -eq $relativePath)
    $file = Join-Path $securityRuntime $relativePath
    if ($records.Count -ne 1 -or -not (Test-Path -LiteralPath $file) -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $records[0].sha256) {
        throw "The parser security component hash is invalid: $relativePath"
    }
    $securityOverrides[[IO.Path]::GetFileName($file)] = $file
}

$ArchivePath = (Resolve-Path -LiteralPath $ArchivePath).Path
$Ucrt64Directory = (Resolve-Path -LiteralPath $Ucrt64Directory).Path
$archiveHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash
if ($archiveHash -ne $expectedArchiveHash) {
    throw "The UxPlay archive hash does not match the pinned download ($commit). Re-download it using the command in README.md."
}
$patches = @(
    (Join-Path $PSScriptRoot 'uxplay-winui.patch'),
    (Join-Path $PSScriptRoot 'uxplay-receiver.patch'),
    (Join-Path $PSScriptRoot 'uxplay-renderer-stats.patch'),
    (Join-Path $PSScriptRoot 'uxplay-live-decoder.patch'),
    (Join-Path $PSScriptRoot 'uxplay-mdns.patch'),
    (Join-Path $PSScriptRoot 'uxplay-network-guards.patch'),
    (Join-Path $PSScriptRoot 'uxplay-http-headers.patch'),
    (Join-Path $PSScriptRoot 'uxplay-diagnostics.patch'),
    (Join-Path $PSScriptRoot 'uxplay-pairing-recovery.patch'),
    (Join-Path $PSScriptRoot 'uxplay-release-hardening.patch')
)
# Use a fresh source tree when a patch changes.
$hashes = $archiveHash + ((Get-FileHash -LiteralPath $patches).Hash -join '')
$sha = [Security.Cryptography.SHA256]::Create()
try {
    $key = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::ASCII.GetBytes($hashes))).Replace('-', '').Substring(0, 16)
} finally {
    $sha.Dispose()
}
$build = Join-Path ([IO.Path]::GetFullPath($BuildDirectory)) $key
$source = Join-Path $build "UxPlay-$commit"
$configured = Join-Path $build 'source-ready'
if (-not (Test-Path -LiteralPath $configured)) {
    [void] [IO.Directory]::CreateDirectory($build)
    if (Test-Path -LiteralPath $source) {
        throw "A previous patch attempt left an incomplete source tree at $source. Use a different -BuildDirectory."
    }
    & tar.exe -xf $ArchivePath -C $build
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $source 'uxplay.cpp'))) {
        throw "The archive must contain the pinned UxPlay source ($commit)."
    }
    Push-Location -LiteralPath $source
    try {
        foreach ($patch in $patches) {
            & git apply $patch
            if ($LASTEXITCODE -ne 0) { throw "Could not apply $patch." }
        }
    } finally {
        Pop-Location
    }
    [void] [IO.File]::Create($configured).Dispose()
}

$bin = Join-Path $Ucrt64Directory 'bin'
$cmake = Join-Path $bin 'cmake.exe'
$objdump = Join-Path $bin 'objdump.exe'
$binaryDir = Join-Path $build 'build'
$originalPath = $env:PATH
try {
    $env:PATH = "$bin;$originalPath"
    & $cmake -S $PSScriptRoot -B $binaryDir -G Ninja -DCMAKE_BUILD_TYPE=Release -DNO_MARCH_NATIVE=ON "-DUXPLAY_SOURCE_DIR=$source"
    if ($LASTEXITCODE -ne 0) { throw 'Receiver configuration failed.' }
    & $cmake --build $binaryDir --target clearcast_receiver --parallel
    if ($LASTEXITCODE -ne 0) { throw 'Receiver build failed.' }

    $receiver = Join-Path $binaryDir 'clearcast_receiver.dll'
    $receiverInfo = & $objdump -p $receiver
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the receiver DLL.' }
    foreach ($export in 'clearcast_receiver_start', 'clearcast_receiver_start_v2', 'clearcast_receiver_stop',
                         'clearcast_receiver_get_name',
                         'clearcast_receiver_read_event', 'clearcast_receiver_read_stats',
                         'clearcast_receiver_set_decoder', 'clearcast_receiver_set_video_rotation') {
        if (-not ($receiverInfo -match "^\s*\[\s*\d+\].*\s$export\s*$")) {
            throw "Receiver DLL does not export $export."
        }
    }

    $pluginDir = Join-Path $Ucrt64Directory 'lib\gstreamer-1.0'
    # Include the playback graphs, H.264/HEVC hardware and software decode, and Windows audio output.
    $pluginNames = @(
        'app', 'coreelements', 'playback', 'autodetect', 'typefindfunctions',
        'videoparsersbad', 'videoconvertscale', 'd3d11', 'libav',
        'audioconvert', 'audioresample', 'volume', 'level', 'wasapi', 'wasapi2', 'directsound'
    )
    $plugins = @($pluginNames | ForEach-Object {
        $name = "libgst$_.dll"
        if ($securityOverrides.ContainsKey($name)) { $securityOverrides[$name] }
        else { Join-Path $pluginDir $name }
    })
    $pending = [Collections.Generic.Queue[string]]::new()
    foreach ($binary in @($receiver) + $plugins) { $pending.Enqueue($binary) }

    $dependencies = @{}
    while ($pending.Count) {
        $binary = $pending.Dequeue()
        if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw "Missing runtime file: $binary" }
        $imports = & $objdump -p $binary
        if ($LASTEXITCODE -ne 0) { throw "Could not inspect PE imports for $binary." }
        foreach ($line in $imports) {
            if ($line -notmatch '^\s*DLL Name:\s*(\S+)\s*$') { continue }
            $name = $Matches[1]
            if ($dependencies.ContainsKey($name)) { continue }
            $dependency = if ($securityOverrides.ContainsKey($name)) { $securityOverrides[$name] }
                else { Join-Path $bin $name }
            if (Test-Path -LiteralPath $dependency -PathType Leaf) {
                $dependencies[$name] = $dependency
                $pending.Enqueue($dependency)
            } elseif ($name -notmatch '^(api|ext)-ms-' -and
                      -not (Test-Path -LiteralPath (Join-Path ([Environment]::SystemDirectory) $name))) {
                throw "Could not resolve dependency $name imported by $binary."
            }
        }
    }

    $output = [IO.Path]::GetFullPath($OutputDirectory)
    $outputPlugins = Join-Path $output 'lib\gstreamer-1.0'
    [void] [IO.Directory]::CreateDirectory($outputPlugins)
    Copy-Item -LiteralPath @($dependencies.Values) -Destination $output -Force
    Copy-Item -LiteralPath $plugins -Destination $outputPlugins -Force
    Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination (Join-Path $output 'LICENSE-UxPlay.txt') -Force
    Copy-Item -LiteralPath $receiver -Destination $output -Force
    Copy-Item -LiteralPath $securityManifestPath -Destination (Join-Path $output 'gstreamer-security-manifest.json') -Force
    Copy-Item -LiteralPath (Join-Path $securityRuntime 'COPYING') -Destination (Join-Path $output 'LICENSE-GStreamerSecurity.txt') -Force
    Write-Output (Join-Path $output 'clearcast_receiver.dll')
} finally {
    $env:PATH = $originalPath
}
