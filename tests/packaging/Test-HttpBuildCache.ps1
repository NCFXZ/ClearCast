[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BuildDirectory,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [Parameter(Mandatory)] [string] $Report
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$qaPrefix = [IO.Path]::GetFullPath((Join-Path $repository '.runtime')).TrimEnd('\') + '\qa-'
$buildRoot = [IO.Path]::GetFullPath($BuildDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($path in @($buildRoot, $output)) {
    if (-not $path.StartsWith($qaPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Cache mutation tests require dedicated .runtime/qa-* directories.'
    }
}
$builder = Join-Path $repository 'scripts\native\Build-GStreamerHttpSecurity.ps1'
$patch = Join-Path $repository 'src\native\patches\gstreamer-soup-destination.patch'
$options = @{ BuildDirectory = $buildRoot; OutputDirectory = $output }
$checks = @()
& $builder @options | Out-Null
$checks += 'A patched build succeeds'
$markers = @(Get-ChildItem -LiteralPath $buildRoot -Filter 'source-ready' -File -Recurse)
if ($markers.Count -ne 1) { throw 'Use a dedicated cache containing exactly one build identity.' }
$marker = $markers[0].FullName
$original = [IO.File]::ReadAllBytes($marker)
function Reject([string] $Expected) {
    $message = $null
    try { & $builder @options | Out-Null } catch { $message = $_.Exception.Message }
    if (-not $message -or $message -notlike $Expected) { throw "Unexpected cache preflight outcome: $message" }
}
try {
    [IO.File]::WriteAllText($marker, 'invalid cached patch identity')
    Reject '*cached HTTP source marker*'
    $checks += 'A mismatched cached patch identity is rejected'
} finally { [IO.File]::WriteAllBytes($marker, $original) }
$source = Join-Path $markers[0].DirectoryName 'gst-plugins-good-1.28.9'
Push-Location -LiteralPath $source
$reverted = $false
try {
    & git --work-tree=. apply --no-index --reverse $patch
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the unpatched cache fixture.' }
    $reverted = $true
    Reject '*HTTP destination patch was not applied*'
    $checks += 'A reverted security patch with an intact ready marker is rejected'
} finally {
    if ($reverted) {
        & git --work-tree=. apply --no-index $patch
        if ($LASTEXITCODE -ne 0) { throw 'Could not restore the isolated source fixture.' }
    }
    Pop-Location
}
& $builder @options | Out-Null
$checks += 'The restored patched cache succeeds'
@{ passed = $true; checks = $checks; buildDirectory = $buildRoot } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $Report
Write-Output "PASS $($checks.Count) HTTP build-cache checks"
