[CmdletBinding()]
param(
    [string] $ArchivePath,
    [string] $Ucrt64Directory,
    [string] $MakePath,
    [string] $DiffDirectory,
    [string] $BuildDirectory,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$runtime = Join-Path $repositoryRoot '.runtime'
$version = '2.8.0'
$sourceUrl = 'https://github.com/libimobiledevice/libplist/releases/download/2.8.0/libplist-2.8.0.tar.bz2'
# Published digest of the official GitHub release asset.
$expectedHash = 'B1F59F7634C58B2481325A23FF4E3BF51574A42D868CBE466D2B39B04550752A'
if (-not $ArchivePath) { $ArchivePath = Join-Path $runtime 'libplist-2.8.0.tar.bz2' }
if (-not $Ucrt64Directory) { $Ucrt64Directory = Join-Path $runtime 'msys64\ucrt64' }
if (-not $MakePath) { $MakePath = Join-Path $runtime 'build-tools\openssl-tools\usr\bin\make.exe' }
if (-not $DiffDirectory) { $DiffDirectory = Join-Path $runtime 'build-tools\libplist-tools\usr\bin' }
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $runtime 'libplist-security-build' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $runtime 'libplist-security' }

$archive = (Resolve-Path -LiteralPath $ArchivePath).Path
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'The libplist source archive does not match the official release digest.'
}
$ucrt = (Resolve-Path -LiteralPath $Ucrt64Directory).Path
$make = (Resolve-Path -LiteralPath $MakePath).Path
$diff = (Resolve-Path -LiteralPath $DiffDirectory).Path
foreach ($tool in 'diff.exe', 'cmp.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $diff $tool) -PathType Leaf)) { throw "Missing upstream-test tool: $tool" }
}
$bash = Join-Path (Split-Path -Parent $ucrt) 'usr\bin\bash.exe'
if (-not (Test-Path -LiteralPath $bash -PathType Leaf)) { throw 'MSYS2 bash is required.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$buildRoot = Join-Path ([IO.Path]::GetFullPath($BuildDirectory)) $expectedHash.Substring(0, 16)
$source = Join-Path $buildRoot "libplist-$version"
$build = Join-Path $buildRoot 'build'
$prefix = Join-Path $output 'prefix'
[void] [IO.Directory]::CreateDirectory($build)
[void] [IO.Directory]::CreateDirectory($output)
if (-not (Test-Path -LiteralPath (Join-Path $buildRoot 'source-ready'))) {
    if (Test-Path -LiteralPath $source) {
        throw 'Incomplete source extraction exists. Use a new BuildDirectory.'
    }
    & tar.exe -xf $archive -C $buildRoot
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract the verified libplist release.' }
    [void] [IO.File]::Create((Join-Path $buildRoot 'source-ready')).Dispose()
}

$script = Join-Path $buildRoot 'build-libplist.sh'
$commands = @'
set -euo pipefail
task_ucrt=$(/usr/bin/cygpath -u "$CLEARCAST_LIBPLIST_UCRT")
task_make=$(/usr/bin/cygpath -u "$CLEARCAST_LIBPLIST_MAKE")
task_source=$(/usr/bin/cygpath -u "$CLEARCAST_LIBPLIST_SOURCE")
task_build=$(/usr/bin/cygpath -u "$CLEARCAST_LIBPLIST_BUILD")
task_prefix=$(/usr/bin/cygpath -u "$CLEARCAST_LIBPLIST_PREFIX")
task_diff=$(/usr/bin/cygpath -u "$CLEARCAST_LIBPLIST_DIFF")
export PATH="$task_ucrt/bin:$task_diff:$(/usr/bin/dirname "$task_make"):/usr/bin:/bin"
export CC="$task_ucrt/bin/gcc.exe"
export CXX="$task_ucrt/bin/g++.exe"
export CFLAGS='-O2'
export CXXFLAGS='-O2'
cd "$task_build"
"$task_source/configure" --prefix="$task_prefix" --build=x86_64-w64-mingw32 --host=x86_64-w64-mingw32 --target=x86_64-w64-mingw32 --without-cython --enable-shared --disable-static
"$task_make" -j"$CLEARCAST_LIBPLIST_JOBS"
"$task_make" check
"$task_make" install
'@
[IO.File]::WriteAllText($script, $commands.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
$settings = @{
    CLEARCAST_LIBPLIST_UCRT = $ucrt
    CLEARCAST_LIBPLIST_MAKE = $make
    CLEARCAST_LIBPLIST_SOURCE = $source
    CLEARCAST_LIBPLIST_BUILD = $build
    CLEARCAST_LIBPLIST_PREFIX = $prefix
    CLEARCAST_LIBPLIST_DIFF = $diff
    CLEARCAST_LIBPLIST_SCRIPT = $script
    CLEARCAST_LIBPLIST_JOBS = [Math]::Min(8, [Environment]::ProcessorCount).ToString()
    MSYSTEM = 'UCRT64'
}
$previous = @{}
foreach ($key in $settings.Keys) {
    $previous[$key] = [Environment]::GetEnvironmentVariable($key)
    [Environment]::SetEnvironmentVariable($key, $settings[$key])
}
try {
    & $bash --noprofile --norc -c 'exec /usr/bin/bash "$(/usr/bin/cygpath -u "$CLEARCAST_LIBPLIST_SCRIPT")"'
    if ($LASTEXITCODE -ne 0) { throw 'The libplist build or upstream test suite failed.' }
} finally {
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key]) }
}

$library = Join-Path $prefix 'bin\libplist-2.0.dll'
if (-not (Test-Path -LiteralPath $library -PathType Leaf)) { throw 'The expected Windows libplist ABI name was not built.' }
Copy-Item -LiteralPath $library -Destination $output -Force
foreach ($name in 'COPYING', 'COPYING.LESSER') {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination $output -Force
}
foreach ($name in 'jsmn', 'time64') {
    $noticeSource = [IO.File]::ReadAllText((Join-Path $source ("src\$name.c")))
    $notice = [regex]::Match($noticeSource, '(?s)\A\s*/\*.*?\*/')
    if (-not $notice.Success -or $notice.Value -notmatch 'Permission is hereby granted' -or
        $notice.Value -notmatch 'THE SOFTWARE IS PROVIDED') {
        throw "The embedded $name MIT notice was not found in the pinned libplist source."
    }
    [IO.File]::WriteAllText((Join-Path $output ("$name-MIT.txt")), $notice.Value.Trim() + "`n", [Text.UTF8Encoding]::new($false))
}
Copy-Item -LiteralPath (Join-Path $build 'test\test-suite.log') -Destination (Join-Path $output 'upstream-tests.log') -Force
$buildFiles = @('prefix/include/plist/plist.h', 'prefix/lib/libplist-2.0.dll.a', 'prefix/lib/pkgconfig/libplist-2.0.pc')
$manifest = [ordered]@{
    schemaVersion = 1
    component = 'libplist'
    version = $version
    sourceUrl = $sourceUrl
    sourceSha256 = $expectedHash.ToLowerInvariant()
    prefixRelativePath = 'prefix'
    files = @([ordered]@{ relativePath = 'libplist-2.0.dll'; sha256 = (Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash.ToLowerInvariant() })
    buildFiles = @($buildFiles | ForEach-Object { [ordered]@{ relativePath = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $output $_) -Algorithm SHA256).Hash.ToLowerInvariant() } })
    build = [ordered]@{
        script = 'scripts/native/Build-LibplistSecurity.ps1'
        target = 'x86_64-w64-mingw32 UCRT64'
        flags = @('-O2', '--without-cython', '--enable-shared', '--disable-static')
        upstreamTests = 'make check (all failures stop the build)'
    }
}
[IO.File]::WriteAllText((Join-Path $output 'manifest.json'), ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
Write-Output (Join-Path $output 'manifest.json')
