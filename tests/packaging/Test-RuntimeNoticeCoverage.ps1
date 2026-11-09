[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PublishDirectory,
    [Parameter(Mandatory)] [string] $StorePublishDirectory,
    [Parameter(Mandatory)] [string] $ComplianceDirectory,
    [Parameter(Mandatory)] [string] $ValidatedReceiverSha256,
    [string] $Version = '1.2.0.0',
    [Parameter(Mandatory)] [string] $OutputPath
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$runtime = [IO.Path]::GetFullPath((Join-Path $repository '.runtime'))
$work = Join-Path $runtime ('notice-coverage-' + [Guid]::NewGuid().ToString('N'))
$fixture = Join-Path $work 'notices'
$neverOutput = Join-Path $work 'no-package-output'
[void] [IO.Directory]::CreateDirectory($work)
Copy-Item -LiteralPath $ComplianceDirectory -Destination $fixture -Recurse
$options = @{
    PublishDirectory = $PublishDirectory; StorePublishDirectory = $StorePublishDirectory
    ComplianceDirectory = $fixture; OutputDirectory = $neverOutput
    # ValidateOnly resolves these paths but must never execute a packaging tool.
    MakeAppxPath = $PSCommandPath; WixPath = $PSCommandPath; WixBalExtensionPath = $PSCommandPath
    ValidatedReceiverSha256 = $ValidatedReceiverSha256; Version = $Version; ValidateOnly = $true
}
$builder = Join-Path $repository 'scripts/packaging/Build-Packages.ps1'
$checks = @()
$report = [ordered]@{ passed = $false; checks = @(); error = $null; modifiesOnlyDisposableNotices = $true }
function Reject([string] $Name, [scriptblock] $Mutate, [scriptblock] $Restore, [string] $Expected) {
    try {
        & $Mutate
        $reason = $null
        try { & $builder @options | Out-Null } catch { $reason = $_.Exception.Message }
        if (-not $reason -or $reason -notlike $Expected) { throw "Unexpected coverage outcome: $Name $reason" }
        $script:checks += [pscustomobject]@{ name = $Name; passed = $true; rejection = $reason }
    } finally { & $Restore }
}
try {
    & $builder @options | Out-Null
    $checks += [pscustomobject]@{ name = 'Complete actual notices accepted'; passed = $true }
    $inventory = Join-Path $fixture 'THIRD-PARTY-COMPONENTS.md'
    $originalInventory = [IO.File]::ReadAllBytes($inventory)
    Reject 'Stale inventory text rejected' { [IO.File]::AppendAllText($inventory, "`nSTALE FIXTURE") } { [IO.File]::WriteAllBytes($inventory, $originalInventory) } '*Source coverage does not match*'
    $coverage = Join-Path $fixture 'source-coverage.json'
    $originalCoverage = [IO.File]::ReadAllBytes($coverage)
    Reject 'Empty source coverage rejected' { [IO.File]::WriteAllText($coverage, '{}') } { [IO.File]::WriteAllBytes($coverage, $originalCoverage) } '*Source coverage does not match*'
    Reject 'Wrong covered file count rejected' {
        $value = Get-Content -LiteralPath $coverage -Raw | ConvertFrom-Json
        $value.fileCount++
        $value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $coverage
    } { [IO.File]::WriteAllBytes($coverage, $originalCoverage) } '*Source coverage does not match*'
    Reject 'Wrong source archive digest rejected' {
        $value = Get-Content -LiteralPath $coverage -Raw | ConvertFrom-Json
        @($value.additionalSources.PSObject.Properties)[0].Value.sha256 = '0' * 64
        $value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $coverage
    } { [IO.File]::WriteAllBytes($coverage, $originalCoverage) } '*additional source archive*'
    $notice = @(Get-ChildItem -LiteralPath (Join-Path $fixture 'licenses') -Filter source-build.json -Recurse -File |
        Where-Object { $_.Directory.Name -like 'mingw-w64-*' })[0].FullName
    $originalNotice = [IO.File]::ReadAllBytes($notice)
    Reject 'Wrong package source record rejected' {
        $value = Get-Content -LiteralPath $notice -Raw | ConvertFrom-Json
        $value.files[0].sha256 = '0' * 64
        $value | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $notice
    } { [IO.File]::WriteAllBytes($notice, $originalNotice) } '*package source record does not match*'
    Reject 'Preflight fixtures cannot create packages' { $options.ValidateOnly = $false; $options.PreflightFixture = $true } {
        $options.ValidateOnly = $true
        $options.Remove('PreflightFixture')
    } '*Test-only preflight fixtures cannot produce*'
    & $builder @options | Out-Null
    if (Test-Path -LiteralPath $neverOutput) { throw 'Read-only coverage validation created package output.' }
    $checks += [pscustomobject]@{ name = 'Restored complete notices accepted without package output'; passed = $true }
    $report.passed = $true
} catch { $report.error = $_.Exception.Message; throw }
finally {
    $report.checks = $checks
    $reportPath = [IO.Path]::GetFullPath($OutputPath)
    [void] [IO.Directory]::CreateDirectory((Split-Path -Parent $reportPath))
    $report | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
    $resolved = [IO.Path]::GetFullPath($work)
    if (-not $resolved.StartsWith($runtime.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unsafe coverage fixture cleanup path.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
$checks | Select-Object name,passed
