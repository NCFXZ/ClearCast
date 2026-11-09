[CmdletBinding()]
param(
    [string] $ArchivePath,
    [string] $Ucrt64Directory,
    [string] $BuildDirectory,
    [string] $OutputDirectory,
    [string] $SecurityRuntimeDirectory,
    [string] $LibplistSecurityDirectory,
    [string] $OpenSslSecurityDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$nativeSourceDirectory = Join-Path $repositoryRoot 'src\native'
$patchDirectory = Join-Path $nativeSourceDirectory 'patches'
$runtime = Join-Path $repositoryRoot '.runtime'
$commit = 'd4b7373cb6107b2d8d6c5838991c19d39e2b96ae'
$expectedArchiveHash = 'D85E077EC7B20D487A2F68BDE7FEBCD1C9941D0F6C83FE740BB10BE3268F47BD'
if (-not $ArchivePath) { $ArchivePath = Join-Path $runtime 'uxplay-d4b7373.tar.gz' }
if (-not $Ucrt64Directory) { $Ucrt64Directory = Join-Path $runtime 'msys64\ucrt64' }
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $runtime 'uxplay-receiver-build' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $runtime 'receiver' }
if (-not $SecurityRuntimeDirectory) { $SecurityRuntimeDirectory = Join-Path $runtime 'gstreamer-security' }
if (-not $LibplistSecurityDirectory) { $LibplistSecurityDirectory = Join-Path $runtime 'libplist-security' }
if (-not $OpenSslSecurityDirectory) { $OpenSslSecurityDirectory = Join-Path $runtime 'openssl-security' }

function Resolve-ComponentFile([string] $Root, [string] $RelativePath) {
    if ([IO.Path]::IsPathRooted($RelativePath)) { throw 'A dependency manifest contains an absolute path.' }
    $resolved = [IO.Path]::GetFullPath((Join-Path $Root $RelativePath))
    $boundary = $Root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A dependency manifest path escapes its component directory.'
    }
    return $resolved
}

function Read-SecurityComponent([string] $Directory, [string] $Name, [string] $Version, [string] $SourceHash) {
    $componentRoot = [IO.Path]::GetFullPath($Directory)
    $manifestPath = Join-Path $componentRoot 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Build the controlled $Name runtime before building the receiver."
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $componentName = if ($manifest.component) { $manifest.component } else { $manifest.name }
    if ($manifest.schemaVersion -ne 1 -or $componentName -ne $Name -or $manifest.version -ne $Version -or
        $manifest.sourceSha256 -ne $SourceHash -or -not $manifest.sourceUrl) {
        throw "The controlled $Name manifest does not match the pinned release."
    }
    foreach ($record in @($manifest.files) + @($manifest.buildFiles)) {
        $file = Resolve-ComponentFile $componentRoot $record.relativePath
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $record.sha256) {
            throw "The controlled $Name file hash is invalid: $($record.relativePath)"
        }
    }
    if (-not $manifest.files -or -not $manifest.buildFiles) { throw "The controlled $Name manifest is incomplete." }
    foreach ($record in $manifest.files) {
        if ([IO.Path]::GetFileName($record.relativePath) -ne $record.relativePath -or
            $record.relativePath -notin @('libplist-2.0.dll', 'libcrypto-3-x64.dll', 'libssl-3-x64.dll')) {
            throw "Unexpected runtime file in the controlled $Name manifest."
        }
    }
    $prefix = Resolve-ComponentFile $componentRoot $manifest.prefixRelativePath
    if (-not (Test-Path -LiteralPath $prefix -PathType Container)) { throw "The controlled $Name build prefix is missing." }
    return [pscustomobject]@{ Name = $Name; Root = $componentRoot; ManifestPath = $manifestPath; Manifest = $manifest; Prefix = $prefix }
}

$components = @(
    (Read-SecurityComponent $LibplistSecurityDirectory 'libplist' '2.8.0' 'B1F59F7634C58B2481325A23FF4E3BF51574A42D868CBE466D2B39B04550752A'),
    (Read-SecurityComponent $OpenSslSecurityDirectory 'openssl' '3.5.9' '603F5602E2EEF00D77FBD429D34DCD5822BB301757A1BC9CDB24C670F1EB859A')
)

# Keep the pinned parser build and its source/hash evidence even when UCRT64
# already supplies 1.28.9, including the H.264/H.265/MPEG parser security fixes.
$securityRuntime = [IO.Path]::GetFullPath($SecurityRuntimeDirectory)
$securityManifestPath = Join-Path $securityRuntime 'manifest.json'
if (-not (Test-Path -LiteralPath $securityManifestPath)) {
    throw 'Build the pinned parser security components with scripts\native\Build-GStreamerSecurity.ps1 first.'
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
foreach ($component in $components) {
    foreach ($record in $component.Manifest.files) {
        $securityOverrides[$record.relativePath] = Resolve-ComponentFile $component.Root $record.relativePath
    }
}

$ArchivePath = (Resolve-Path -LiteralPath $ArchivePath).Path
$Ucrt64Directory = (Resolve-Path -LiteralPath $Ucrt64Directory).Path
$archiveHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash
if ($archiveHash -ne $expectedArchiveHash) {
    throw "The UxPlay archive hash does not match the pinned download ($commit). Re-download it using the command in README.md."
}
$patches = @(
    (Join-Path $patchDirectory 'uxplay-winui.patch'),
    (Join-Path $patchDirectory 'uxplay-receiver.patch'),
    (Join-Path $patchDirectory 'uxplay-renderer-stats.patch'),
    (Join-Path $patchDirectory 'uxplay-live-decoder.patch'),
    (Join-Path $patchDirectory 'uxplay-mdns.patch'),
    (Join-Path $patchDirectory 'uxplay-network-guards.patch'),
    (Join-Path $patchDirectory 'uxplay-http-headers.patch'),
    (Join-Path $patchDirectory 'uxplay-diagnostics.patch'),
    (Join-Path $patchDirectory 'uxplay-pairing-recovery.patch'),
    (Join-Path $patchDirectory 'uxplay-release-hardening.patch'),
    (Join-Path $patchDirectory 'uxplay-pairing-feedback.patch'),
    (Join-Path $patchDirectory 'uxplay-srp-salt.patch'),
    (Join-Path $patchDirectory 'uxplay-media-video.patch'),
    (Join-Path $patchDirectory 'uxplay-media-audio.patch'),
    (Join-Path $patchDirectory 'uxplay-first-pin-retry.patch'),
    (Join-Path $patchDirectory 'uxplay-pairing-dual-stack.patch'),
    (Join-Path $patchDirectory 'uxplay-pairing-cooldown-feedback.patch'),
    (Join-Path $patchDirectory 'uxplay-audio-activity.patch')
)
# Use a fresh source tree when a patch changes.
$hashes = $archiveHash + ((Get-FileHash -LiteralPath $patches).Hash -join '') +
    ((Get-FileHash -LiteralPath @($components.ManifestPath)).Hash -join '') +
    ((Get-FileHash -LiteralPath @((Get-ChildItem -LiteralPath $nativeSourceDirectory -Filter '*.h' -File | Sort-Object Name).FullName +
        (Join-Path $nativeSourceDirectory 'CMakeLists.txt'))).Hash -join '')
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
$testPython = (Get-Command python -ErrorAction SilentlyContinue).Source
$originalPath = $env:PATH
$originalPkgConfigPath = $env:PKG_CONFIG_PATH
try {
    $libplistPrefix = $components[0].Prefix
    $opensslPrefix = $components[1].Prefix
    $plistImportLibrary = Join-Path $libplistPrefix 'lib\libplist-2.0.dll.a'
    if (-not (Test-Path -LiteralPath $plistImportLibrary -PathType Leaf)) { throw 'The controlled libplist import library is missing.' }
    $cryptoLibraries = @(Get-ChildItem -LiteralPath $opensslPrefix -Recurse -Filter 'libcrypto.dll.a' -File)
    $sslLibraries = @(Get-ChildItem -LiteralPath $opensslPrefix -Recurse -Filter 'libssl.dll.a' -File)
    if ($cryptoLibraries.Count -ne 1 -or $sslLibraries.Count -ne 1) { throw 'The controlled OpenSSL import libraries are missing or ambiguous.' }
    $env:PATH = "$($components[0].Root);$($components[1].Root);$bin;$originalPath"
    $env:PKG_CONFIG_PATH = (Join-Path $libplistPrefix 'lib\pkgconfig') + $(if ($originalPkgConfigPath) { ";$originalPkgConfigPath" })
    $testOptions = @()
    if ($testPython) { $testOptions += "-DPython3_EXECUTABLE=$testPython" }
    & $cmake -S $nativeSourceDirectory -B $binaryDir -G Ninja -DCMAKE_BUILD_TYPE=Release -DNO_MARCH_NATIVE=ON "-DUXPLAY_SOURCE_DIR=$source" `
        "-DLIBPLIST=$plistImportLibrary" `
        "-DOPENSSL_ROOT_DIR=$opensslPrefix" "-DOPENSSL_INCLUDE_DIR=$(Join-Path $opensslPrefix 'include')" `
        "-DOPENSSL_CRYPTO_LIBRARY=$($cryptoLibraries[0].FullName)" "-DOPENSSL_SSL_LIBRARY=$($sslLibraries[0].FullName)" @testOptions
    if ($LASTEXITCODE -ne 0) { throw 'Receiver configuration failed.' }
    & $cmake --build $binaryDir --target clearcast_receiver --parallel
    if ($LASTEXITCODE -ne 0) { throw 'Receiver build failed.' }

    $receiver = Join-Path $binaryDir 'clearcast_receiver.dll'
    $receiverInfo = & $objdump -p $receiver
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the receiver DLL.' }
    foreach ($export in 'clearcast_receiver_start', 'clearcast_receiver_start_v2', 'clearcast_receiver_stop',
                         'clearcast_receiver_get_name',
                         'clearcast_receiver_read_event', 'clearcast_receiver_read_stats',
                         'clearcast_receiver_set_decoder', 'clearcast_receiver_set_video_rotation',
                         'clearcast_receiver_read_media', 'clearcast_receiver_read_artwork', 'clearcast_receiver_media_command') {
        if (-not ($receiverInfo -match "^\s*\[\s*\d+\].*\s$export\s*$")) {
            throw "Receiver DLL does not export $export."
        }
    }

    $pluginDir = Join-Path $Ucrt64Directory 'lib\gstreamer-1.0'
    $scanner = Join-Path $Ucrt64Directory 'libexec\gstreamer-1.0\gst-plugin-scanner.exe'
    # Mirroring/audio plus direct HTTP(S) MP4/HLS. Use the legacy HLS demuxer so
    # its private URI downloads use the same receiver-controlled HTTP factory.
    $pluginNames = @(
        'app', 'coreelements', 'playback', 'autodetect', 'typefindfunctions',
        'videoparsersbad', 'videoconvertscale', 'd3d11', 'libav',
        'audioconvert', 'audioresample', 'volume', 'level', 'wasapi', 'wasapi2', 'directsound',
        'soup', 'hls', 'isomp4', 'mpegtsdemux', 'audioparsers', 'subparse'
    )
    $plugins = @($pluginNames | ForEach-Object {
        $name = "libgst$_.dll"
        if ($securityOverrides.ContainsKey($name)) { $securityOverrides[$name] }
        else { Join-Path $pluginDir $name }
    })
    $tlsModule = Join-Path $Ucrt64Directory 'lib\gio\modules\libgiognutls.dll'
    $certificateBundle = Join-Path $Ucrt64Directory 'etc\ssl\certs\ca-bundle.crt'
    foreach ($file in @($tlsModule, $certificateBundle)) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing HTTPS runtime file: $file" }
    }
    $pending = [Collections.Generic.Queue[string]]::new()
    foreach ($binary in @($receiver, $scanner, $tlsModule) + $plugins) { $pending.Enqueue($binary) }

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
    $outputTlsModules = Join-Path $output 'lib\gio\modules'
    $outputCertificates = Join-Path $output 'certs'
    [void] [IO.Directory]::CreateDirectory($outputTlsModules)
    [void] [IO.Directory]::CreateDirectory($outputCertificates)
    Copy-Item -LiteralPath $tlsModule -Destination $outputTlsModules -Force
    [IO.File]::WriteAllText((Join-Path $outputTlsModules 'giomodule.cache'), "libgiognutls.dll: gio-tls-backend`n", [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath $certificateBundle -Destination (Join-Path $outputCertificates 'ca-bundle.crt') -Force
    Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination (Join-Path $output 'LICENSE-UxPlay.txt') -Force
    Copy-Item -LiteralPath $receiver -Destination $output -Force
    Copy-Item -LiteralPath $scanner -Destination $output -Force
    Copy-Item -LiteralPath $securityManifestPath -Destination (Join-Path $output 'gstreamer-security-manifest.json') -Force
    Copy-Item -LiteralPath (Join-Path $securityRuntime 'COPYING') -Destination (Join-Path $output 'LICENSE-GStreamerSecurity.txt') -Force
    $publishedComponents = @($components | ForEach-Object {
        $component = $_
        $publishedFiles = @($component.Manifest.files | Where-Object { $dependencies.ContainsKey($_.relativePath) })
        if (-not $publishedFiles) { throw 'A controlled dependency was not used by the receiver build.' }
        [ordered]@{
            name = $component.Name
            version = $component.Manifest.version
            sourceUrl = $component.Manifest.sourceUrl
            sourceSha256 = $component.Manifest.sourceSha256
            files = $publishedFiles
            build = $component.Manifest.build
        }
    })
    [IO.File]::WriteAllText((Join-Path $output 'security-dependencies-manifest.json'),
        ([ordered]@{ schemaVersion = 1; components = $publishedComponents } | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath (Join-Path $components[0].Root 'COPYING.LESSER') -Destination (Join-Path $output 'LICENSE-Libplist.txt') -Force
    $opensslLicense = @('LICENSE.txt', 'LICENSE') | ForEach-Object { Join-Path $components[1].Root $_ } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $opensslLicense) { throw 'The controlled OpenSSL license is missing.' }
    Copy-Item -LiteralPath $opensslLicense -Destination (Join-Path $output 'LICENSE-OpenSSL.txt') -Force
    # Check every consumer in the completed PE closure against the exact new
    # crypto exports. A first dependency build may only have checked itself.
    $opensslVerifier = Join-Path $components[1].Root 'verify-imports.py'
    $pePython = Join-Path $bin 'python.exe'
    if (-not (Test-Path -LiteralPath $opensslVerifier -PathType Leaf) -or
        -not (Test-Path -LiteralPath $pePython -PathType Leaf)) {
        throw 'The controlled OpenSSL PE compatibility verifier and UCRT64 Python are required.'
    }
    & $pePython -I -S $opensslVerifier $output $components[1].Root (Join-Path $build 'openssl-compatibility.json')
    if ($LASTEXITCODE -ne 0) { throw 'The completed receiver runtime is incompatible with the controlled OpenSSL DLL.' }
    Write-Output (Join-Path $output 'clearcast_receiver.dll')
} finally {
    $env:PATH = $originalPath
    $env:PKG_CONFIG_PATH = $originalPkgConfigPath
}
