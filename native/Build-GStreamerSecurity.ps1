[CmdletBinding()]
param(
    [string] $ArchivePath,
    [string] $Ucrt64Directory,
    [string] $BuildDirectory,
    [string] $OutputDirectory,
    [string] $MesonModuleDirectory
)

$ErrorActionPreference = 'Stop'
$runtime = Join-Path (Split-Path -Parent $PSScriptRoot) '.runtime'
$version = '1.28.9'
$sourceHash = '4A4E0B7BF84F868378C48F143A7AC3A98923B488A5B74F1A35BA46BC3C43F6B1'
$sourceUrl = "https://gstreamer.freedesktop.org/src/gst-plugins-bad/gst-plugins-bad-$version.tar.xz"
if (-not $ArchivePath) { $ArchivePath = Join-Path $runtime "gst-plugins-bad-$version.tar.xz" }
if (-not $Ucrt64Directory) { $Ucrt64Directory = Join-Path $runtime 'msys64\ucrt64' }
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $runtime "gstreamer-security-build\$version" }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $runtime 'gstreamer-security' }
$archive = [IO.Path]::GetFullPath($ArchivePath)
if (-not (Test-Path -LiteralPath $archive)) {
    [void] [IO.Directory]::CreateDirectory((Split-Path -Parent $archive))
    Invoke-WebRequest $sourceUrl -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $sourceHash) {
    throw 'The GStreamer source archive does not match the pinned official SHA256.'
}
$ucrt = (Resolve-Path -LiteralPath $Ucrt64Directory).Path
$bin = Join-Path $ucrt 'bin'
$python = Join-Path $bin 'python.exe'
$meson = Join-Path $bin 'meson.exe'
if ($MesonModuleDirectory) {
    $mesonModule = (Resolve-Path -LiteralPath $MesonModuleDirectory).Path
    if (-not (Test-Path -LiteralPath (Join-Path $mesonModule 'mesonbuild\mesonmain.py'))) {
        throw 'MesonModuleDirectory must contain the mesonbuild package.'
    }
} elseif (-not (Test-Path -LiteralPath $meson)) {
    throw 'Install mingw-w64-ucrt-x86_64-meson, or provide -MesonModuleDirectory.'
}
$buildRoot = [IO.Path]::GetFullPath($BuildDirectory)
[void] [IO.Directory]::CreateDirectory($buildRoot)
$source = Join-Path $buildRoot "gst-plugins-bad-$version"
if (-not (Test-Path -LiteralPath (Join-Path $source 'meson.build'))) {
    & tar.exe -xf $archive -C $buildRoot
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract the pinned GStreamer source.' }
}
$build = Join-Path $buildRoot 'build'
$savedEnvironment = @{}
foreach ($name in 'PATH','PYTHONPATH','CC','CXX','PKG_CONFIG') {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
try {
    $env:PATH = "$bin;$env:PATH"
    $env:CC = Join-Path $bin 'gcc.exe'
    $env:CXX = Join-Path $bin 'g++.exe'
    $env:PKG_CONFIG = Join-Path $bin 'pkg-config.exe'
    if ($MesonModuleDirectory) { $env:PYTHONPATH = $mesonModule }
    $mesonCommand = if ($MesonModuleDirectory) { $python } else { $meson }
    $mesonPrefix = if ($MesonModuleDirectory) { @('-m','mesonbuild.mesonmain') } else { @() }
    if (-not (Test-Path -LiteralPath (Join-Path $build 'build.ninja'))) {
        & $mesonCommand @mesonPrefix setup $build $source --buildtype=release --wrap-mode=nofallback `
            -Dauto_features=disabled -Dvideoparsers=enabled -Dtests=disabled -Dexamples=disabled `
            -Ddoc=disabled -Dintrospection=disabled
        if ($LASTEXITCODE -ne 0) { throw 'Could not configure the GStreamer security components.' }
    }
    $targets = @(
        'gst-libs/gst/codecparsers/libgstcodecparsers-1.0-0.dll',
        'gst/videoparsers/libgstvideoparsersbad.dll'
    )
    & (Join-Path $bin 'ninja.exe') -C $build @targets
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the GStreamer security components.' }
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    [void] [IO.Directory]::CreateDirectory((Join-Path $output 'lib\gstreamer-1.0'))
    $files = @(
        @{ Source = $targets[0]; RelativePath = 'libgstcodecparsers-1.0-0.dll' },
        @{ Source = $targets[1]; RelativePath = 'lib/gstreamer-1.0/libgstvideoparsersbad.dll' }
    )
    $inventory = foreach ($file in $files) {
        $destination = Join-Path $output $file.RelativePath
        Copy-Item -LiteralPath (Join-Path $build $file.Source) -Destination $destination -Force
        [ordered]@{ relativePath = $file.RelativePath; sha256 = (Get-FileHash -LiteralPath $destination).Hash }
    }
    Copy-Item -LiteralPath (Join-Path $source 'COPYING') -Destination (Join-Path $output 'COPYING') -Force
    [ordered]@{
        schemaVersion = 1
        version = $version
        sourceUrl = $sourceUrl
        sourceSha256 = $sourceHash
        license = 'LGPL-2.1-or-later'
        configuration = 'release; auto_features=disabled; videoparsers=enabled; tests/examples/doc/introspection=disabled; wrap-mode=nofallback'
        files = @($inventory)
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'manifest.json') -Encoding utf8NoBOM
    Get-Item -LiteralPath (Join-Path $output 'manifest.json')
} finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name])
    }
}
