[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PublishDirectory,
    [Parameter(Mandatory)] [string] $StorePublishDirectory,
    [Parameter(Mandatory)] [string] $ComplianceDirectory,
    [Parameter(Mandatory)] [string] $ValidatedReceiverSha256,
    [string] $UiTestPublishDirectory,
    [string] $Version = '1.3.1.0',
    [Parameter(Mandatory)] [string] $OutputPath
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$runtime = (Resolve-Path -LiteralPath (Join-Path $repository '.runtime')).Path.TrimEnd('\') + '\'
$work = Join-Path $runtime ('package-preflight-' + [Guid]::NewGuid().ToString('N'))
[void] [IO.Directory]::CreateDirectory($work)
$results = @()
$builder = Join-Path $repository 'scripts\packaging\Build-Packages.ps1'
try {
    # Copy the files inspected by ValidateOnly, never mutate the release payload.
    foreach ($payloadInput in @(
        @{ Source = $PublishDirectory; Name = 'desktop' },
        @{ Source = $StorePublishDirectory; Name = 'store' }
    )) {
        $destination = Join-Path $work $payloadInput.Name
        foreach ($relative in @('ClearCast.exe', 'ClearCast.dll', 'ClearCast.pri', 'resources.pri',
            'Assets', 'LICENSE', 'PRIVACY.md', 'THIRD-PARTY-NOTICES.md',
            'receiver\clearcast_receiver.dll', 'receiver\gst-plugin-scanner.exe',
            'receiver\gstreamer-security-manifest.json', 'receiver\security-dependencies-manifest.json',
            'receiver\gstreamer-http-security-manifest.json', 'receiver\LICENSE-GStreamerHttpSecurity.txt',
            'receiver\lib\gstreamer-1.0\libgstsoup.dll',
            'receiver\libplist-2.0.dll', 'receiver\libcrypto-3-x64.dll', 'receiver\libssl-3-x64.dll',
            'receiver\libgstcodecparsers-1.0-0.dll',
            'receiver\libgstadaptivedemux-1.0-0.dll', 'receiver\LICENSE-GStreamerSecurity.txt',
            'receiver\lib\gstreamer-1.0\libgstvideoparsersbad.dll')) {
            $source = Join-Path $payloadInput.Source $relative
            if (-not (Test-Path -LiteralPath $source)) { continue }
            $target = Join-Path $destination $relative
            [void] [IO.Directory]::CreateDirectory((Split-Path -Parent $target))
            Copy-Item -LiteralPath $source -Destination $target -Recurse
        }
    }
    Copy-Item -LiteralPath $ComplianceDirectory -Destination (Join-Path $work 'notices') -Recurse
    $options = @{
        PublishDirectory = Join-Path $work 'desktop'; StorePublishDirectory = Join-Path $work 'store'
        ComplianceDirectory = Join-Path $work 'notices'; OutputDirectory = Join-Path $work 'output'
        Version = $Version; ValidatedReceiverSha256 = $ValidatedReceiverSha256
        # ValidateOnly resolves these paths but must never execute packaging tools.
        MakeAppxPath = $PSCommandPath; WixPath = $PSCommandPath; WixBalExtensionPath = $PSCommandPath
        ValidateOnly = $true
        PreflightFixture = $true
    }
    function Check-Rejected([string] $Name, [scriptblock] $Mutation, [scriptblock] $Restore, [string] $Message) {
        try {
            & $Mutation
            $rejected = $null
            try { & $builder @options | Out-Null } catch { $rejected = $_.Exception.Message }
            if (-not $rejected -or $rejected -notlike $Message) { throw "Unexpected preflight outcome for ${Name}: $rejected" }
            $script:results += [pscustomobject]@{ name = $Name; passed = $true; rejection = $rejected }
        } finally { & $Restore }
    }
    & $builder @options | Out-Null
    $results += [pscustomobject]@{ name = 'Unmodified release accepted'; passed = $true }
    $publishState = Join-Path $work 'desktop\.clearcast-state'
    $publishSecret = Join-Path $publishState 'receiver.pem'
    Check-Rejected 'Publish pairing state rejected' {
        [void] [IO.Directory]::CreateDirectory($publishState)
        [IO.File]::WriteAllText($publishSecret, 'private fixture; must not ship')
    } {
        Remove-Item -LiteralPath $publishSecret
        Remove-Item -LiteralPath $publishState
    } '*Private/generated state in package input*'
    $complianceState = Join-Path $work 'notices\licenses\.RUNTIME'
    $complianceSecret = Join-Path $complianceState 'ui-error.log'
    Check-Rejected 'Nested compliance state rejected' {
        [void] [IO.Directory]::CreateDirectory($complianceState)
        [IO.File]::WriteAllText($complianceSecret, 'private fixture; must not ship')
    } {
        Remove-Item -LiteralPath $complianceSecret
        Remove-Item -LiteralPath $complianceState
    } '*Private/generated state in package input*'
    if ($UiTestPublishDirectory) {
        $uiAssembly = Join-Path $UiTestPublishDirectory 'ClearCast.dll'
        $releaseAssembly = Join-Path $work 'store\ClearCast.dll'
        $assemblyBackup = [IO.File]::ReadAllBytes($releaseAssembly)
        Check-Rejected 'UI-test application rejected' {
            Copy-Item -LiteralPath $uiAssembly -Destination $releaseAssembly -Force
        } { [IO.File]::WriteAllBytes($releaseAssembly, $assemblyBackup) } '*UI-test application cannot be packaged*'
    }
    $plist = Join-Path $work 'store\receiver\libplist-2.0.dll'
    $backupPlist = [IO.File]::ReadAllBytes($plist)
    Check-Rejected 'Tampered property-list parser' { [IO.File]::WriteAllBytes($plist, [byte[]]@(1, 2, 3)) } { [IO.File]::WriteAllBytes($plist, $backupPlist) } '*dependency file or notice hash does not match*'
    $crypto = Join-Path $work 'store\receiver\libcrypto-3-x64.dll'
    $backupCrypto = [IO.File]::ReadAllBytes($crypto)
    Check-Rejected 'Tampered cryptography library' { [IO.File]::WriteAllBytes($crypto, [byte[]]@(1, 2, 3)) } { [IO.File]::WriteAllBytes($crypto, $backupCrypto) } '*dependency file or notice hash does not match*'
    Check-Rejected 'Wrong release version' { $options.Version = '9.9.9' } { $options.Version = $Version } '*version*does not match*'
    Check-Rejected 'Untested receiver rejected' { $options.ValidatedReceiverSha256 = '0' * 64 } { $options.ValidatedReceiverSha256 = $ValidatedReceiverSha256 } '*independently validated*'
    $scanner = Join-Path $work 'store\receiver\gst-plugin-scanner.exe'
    $backup = [IO.File]::ReadAllBytes($scanner)
    Check-Rejected 'Missing plugin scanner' { Remove-Item -LiteralPath $scanner } { [IO.File]::WriteAllBytes($scanner, $backup) } '*Missing receiver\gst-plugin-scanner.exe*'
    Check-Rejected 'Mismatched plugin scanner' { [IO.File]::WriteAllBytes($scanner, [byte[]]@(1, 2, 3)) } { [IO.File]::WriteAllBytes($scanner, $backup) } '*runtime files do not match*'
    $frame = Join-Path $work 'store\Assets\MirroringAnimation\frame-079.svg'
    $backupFrame = [IO.File]::ReadAllBytes($frame)
    Check-Rejected 'Missing animation frame' { Remove-Item -LiteralPath $frame } { [IO.File]::WriteAllBytes($frame, $backupFrame) } '*Missing animation frame*'
    $extra = Join-Path $work 'store\Assets\MirroringAnimation\extra.svg'
    Check-Rejected 'Unexpected animation frame' { [IO.File]::WriteAllText($extra, '<svg/>') } { Remove-Item -LiteralPath $extra } '*exactly 200 SVG frames*'
    $parser = Join-Path $work 'store\receiver\libgstcodecparsers-1.0-0.dll'
    $backupParser = [IO.File]::ReadAllBytes($parser)
    Check-Rejected 'Tampered parser binary' { [IO.File]::WriteAllBytes($parser, [byte[]]@(1, 2, 3)) } { [IO.File]::WriteAllBytes($parser, $backupParser) } '*hash does not match*'
    $notice = Join-Path $work 'notices\licenses\gst-plugins-bad-1.28.9\source-build.json'
    $backupNotice = [IO.File]::ReadAllBytes($notice)
    Check-Rejected 'Tampered parser notice' {
        $manifest = Get-Content -LiteralPath $notice -Raw | ConvertFrom-Json
        $manifest.files[0].sha256 = '0' * 64
        $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $notice
    } { [IO.File]::WriteAllBytes($notice, $backupNotice) } '*hash does not match*'
    $httpManifestPath = Join-Path $work 'store\receiver\gstreamer-http-security-manifest.json'
    $httpManifestBackup = [IO.File]::ReadAllBytes($httpManifestPath)
    Check-Rejected 'Missing controlled HTTP manifest' { Remove-Item -LiteralPath $httpManifestPath } {
        [IO.File]::WriteAllBytes($httpManifestPath, $httpManifestBackup)
    } '*Missing receiver\gstreamer-http-security-manifest.json*'
    Check-Rejected 'Wrong controlled HTTP patch provenance' {
        $value = Get-Content -LiteralPath $httpManifestPath -Raw | ConvertFrom-Json
        $value.sourcePatch.sha256 = '0' * 64
        $value | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $httpManifestPath
    } { [IO.File]::WriteAllBytes($httpManifestPath, $httpManifestBackup) } '*HTTP source manifest or notice*'
    $httpBinary = Join-Path $work 'store\receiver\lib\gstreamer-1.0\libgstsoup.dll'
    $httpBinaryBackup = [IO.File]::ReadAllBytes($httpBinary)
    Check-Rejected 'Tampered controlled HTTP source binary' { [IO.File]::WriteAllBytes($httpBinary, [byte[]]@(1,2,3)) } {
        [IO.File]::WriteAllBytes($httpBinary, $httpBinaryBackup)
    } '*HTTP source binary or notice hash does not match*'
    & $builder @options | Out-Null
    if (Test-Path -LiteralPath $options.OutputDirectory) { throw 'ValidateOnly created packaging output.' }
    $results += [pscustomobject]@{ name = 'Restored payload accepted without output'; passed = $true }
    @{ passed = $true; checks = $results } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath
    $results | Format-Table -AutoSize
} finally {
    $target = (Resolve-Path -LiteralPath $work).Path
    if (-not $target.StartsWith($runtime, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $target -Recurse -Force
}
