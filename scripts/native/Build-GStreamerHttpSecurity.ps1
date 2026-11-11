[CmdletBinding()]
param(
    [string] $ArchivePath,
    [string] $Ucrt64Directory,
    [string] $BuildDirectory,
    [string] $OutputDirectory,
    [string] $MesonModuleDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$runtime = Join-Path $repositoryRoot '.runtime'
$version = '1.28.9'
$sourceHash = 'E518CD97483706A252E95E60312A22D23DEC6F5E7673AF6F5BDE719C3A9CA7F4'
$sourceUrl = "https://gstreamer.freedesktop.org/src/gst-plugins-good/gst-plugins-good-$version.tar.xz"
$patchRelativePath = 'src/native/patches/gstreamer-soup-destination.patch'
$patch = Join-Path $repositoryRoot $patchRelativePath
$patchHash = (Get-FileHash -LiteralPath $patch -Algorithm SHA256).Hash
$configuration = 'release; auto_features=disabled; soup=enabled; soup-lookup-dep=true; soup-version=3; tests/examples/doc=disabled; wrap-mode=nofallback; stack-protector-strong; fortify=3; ASLR/NX/high-entropy-va'
if (-not $ArchivePath) { $ArchivePath = Join-Path $runtime "gst-plugins-good-$version.tar.xz" }
if (-not $Ucrt64Directory) { $Ucrt64Directory = Join-Path $runtime 'msys64\ucrt64' }
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $runtime 'gstreamer-http-security-build' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $runtime 'gstreamer-http-security' }
$archive = [IO.Path]::GetFullPath($ArchivePath)
if (-not (Test-Path -LiteralPath $archive)) {
    [void] [IO.Directory]::CreateDirectory((Split-Path -Parent $archive))
    Invoke-WebRequest $sourceUrl -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $sourceHash) {
    throw 'The GStreamer good source archive does not match the pinned official SHA256.'
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
$recipeHash = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
$buildKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($sourceHash + $patchHash + $configuration + $recipeHash))).Substring(0, 16)
$buildRoot = Join-Path ([IO.Path]::GetFullPath($BuildDirectory)) ($version + '-' + $buildKey)
$source = Join-Path $buildRoot "gst-plugins-good-$version"
$ready = Join-Path $buildRoot 'source-ready'
if (-not (Test-Path -LiteralPath $ready)) {
    [void] [IO.Directory]::CreateDirectory($buildRoot)
    if (Test-Path -LiteralPath $source) { throw "Incomplete patched source tree: $source. Use a different -BuildDirectory." }
    & tar.exe -xf $archive -C $buildRoot
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract the pinned GStreamer good source.' }
    Push-Location -LiteralPath $source
    try {
        & git --work-tree=. apply --no-index $patch
        if ($LASTEXITCODE -ne 0) { throw 'Could not apply the controlled HTTP destination patch.' }
    } finally { Pop-Location }
    [IO.File]::WriteAllText($ready, $patchHash)
}
if ([IO.File]::ReadAllText($ready) -ne $patchHash) {
    throw 'The cached HTTP source marker does not match the controlled patch.'
}
Push-Location -LiteralPath $source
try {
    & git --work-tree=. apply --no-index --reverse --check $patch
    if ($LASTEXITCODE -ne 0) { throw 'The HTTP destination patch was not applied to the source tree.' }
} finally { Pop-Location }
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
            -Dauto_features=disabled -Dsoup=enabled -Dsoup-lookup-dep=true -Dsoup-version=3 `
            "-Dc_args=['-fstack-protector-strong','-D_FORTIFY_SOURCE=3']" `
            "-Dc_link_args=['-Wl,--dynamicbase,--nxcompat,--high-entropy-va']" `
            -Dtests=disabled -Dexamples=disabled -Ddoc=disabled
        if ($LASTEXITCODE -ne 0) { throw 'Could not configure the GStreamer HTTP security component.' }
    }
    $target = 'ext/soup/libgstsoup.dll'
    & (Join-Path $bin 'ninja.exe') -C $build $target
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the GStreamer HTTP security component.' }
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    $relativePath = 'lib/gstreamer-1.0/libgstsoup.dll'
    $destination = Join-Path $output $relativePath
    [void] [IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
    Copy-Item -LiteralPath (Join-Path $build $target) -Destination $destination -Force
    Copy-Item -LiteralPath (Join-Path $source 'COPYING') -Destination (Join-Path $output 'COPYING') -Force
    [ordered]@{
        schemaVersion = 1
        version = $version
        sourceUrl = $sourceUrl
        sourceSha256 = $sourceHash
        sourcePatch = [ordered]@{ relativePath = $patchRelativePath; sha256 = $patchHash }
        license = 'LGPL-2.1-or-later'
        configuration = $configuration
        files = @([ordered]@{ relativePath = $relativePath; sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash })
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'manifest.json') -Encoding utf8NoBOM
    Get-Item -LiteralPath (Join-Path $output 'manifest.json')
} finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name])
    }
}
