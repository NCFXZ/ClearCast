[CmdletBinding()]
param(
    [string] $SourceDirectory,
    [string] $Ucrt64Directory,
    [string] $BuildDirectory,
    [switch] $Install
)

$ErrorActionPreference = 'Stop'
$runtime = Join-Path (Split-Path -Parent $PSScriptRoot) '.runtime'
$commit = 'd4b7373cb6107b2d8d6c5838991c19d39e2b96ae'
if (-not $SourceDirectory) { $SourceDirectory = Join-Path $runtime "UxPlay-$commit" }
if (-not $Ucrt64Directory) { $Ucrt64Directory = Join-Path $runtime 'msys64\ucrt64' }
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $runtime 'uxplay-winui-build' }
if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory 'uxplay.cpp'))) {
    throw "Extract or check out UxPlay $commit at $SourceDirectory before building."
}

$SourceDirectory = (Resolve-Path -LiteralPath $SourceDirectory).Path
$Ucrt64Directory = (Resolve-Path -LiteralPath $Ucrt64Directory).Path
$BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
$patch = Join-Path $PSScriptRoot 'uxplay-winui.patch'
Push-Location -LiteralPath $SourceDirectory
try {
    & git apply --check $patch 2>$null
    if ($LASTEXITCODE -eq 0) {
        & git apply $patch
        if ($LASTEXITCODE -ne 0) { throw 'Could not apply the ClearCast patch.' }
    } else {
        & git apply --check --reverse $patch
        if ($LASTEXITCODE -ne 0) {
            throw "The source does not match UxPlay $commit or the already-patched source."
        }
    }
} finally {
    Pop-Location
}

$bin = Join-Path $Ucrt64Directory 'bin'
$cmake = Join-Path $bin 'cmake.exe'
$originalPath = $env:PATH
try {
    $env:PATH = "$bin;$originalPath"
    & $cmake -S $SourceDirectory -B $BuildDirectory -G Ninja -DCMAKE_BUILD_TYPE=Release -DNO_MARCH_NATIVE=ON "-DCMAKE_INSTALL_PREFIX=$Ucrt64Directory"
    if ($LASTEXITCODE -ne 0) { throw 'UxPlay configuration failed.' }
    & $cmake --build $BuildDirectory --parallel
    if ($LASTEXITCODE -ne 0) { throw 'UxPlay build failed.' }
} finally {
    $env:PATH = $originalPath
}

$receiver = Join-Path $BuildDirectory 'uxplay.exe'
if ($Install) {
    $installedReceiver = Join-Path $bin 'uxplay.exe'
    if (Get-Process -Name uxplay -ErrorAction SilentlyContinue | Where-Object Path -eq $installedReceiver) {
        throw 'Stop the running receiver before replacing it.'
    }
    Copy-Item -LiteralPath $receiver -Destination $installedReceiver -Force
    Write-Output $installedReceiver
} else {
    Write-Output $receiver
}
