[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PreviousSetupPath,
    [Parameter(Mandatory)] [string] $SetupPath,
    [Parameter(Mandatory)] [string] $StorePackagePath,
    [Parameter(Mandatory)] [string] $DesktopPublishDirectory,
    [Parameter(Mandatory)] [string] $StorePublishDirectory,
    [Parameter(Mandatory)] [string] $ComplianceDirectory,
    [Parameter(Mandatory)] [string] $SdkToolsDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')] [string] $PreviousVersion = '1.1.0.0',
    [ValidatePattern('^[A-Fa-f0-9]{64}$')] [string] $PreviousSetupSha256 = 'A8801D3963D2ACC754957CF12BB0488A222C2522E406196826020D4FFBF97E98'
)
$ErrorActionPreference = 'Stop'
$previousInstaller = (Resolve-Path -LiteralPath $PreviousSetupPath).Path
if ((Get-FileHash -LiteralPath $previousInstaller -Algorithm SHA256).Hash -ne $PreviousSetupSha256) {
    throw 'The previous installer does not match the independently verified public release.'
}
$storeExtension = [IO.Path]::GetExtension($StorePackagePath).ToLowerInvariant()
if ($storeExtension -notin @('.appx', '.msix')) { throw 'Use an actual MakeAppx-produced APPX or MSIX package.' }
$storeInputName = 'Store' + $storeExtension
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$work = Join-Path $repository ('.runtime\sandbox-validation-' + [Guid]::NewGuid().ToString('N'))
$inputDirectory = Join-Path $work 'input'
$outputDirectory = Join-Path $work 'output'
[void] [IO.Directory]::CreateDirectory($inputDirectory)
[void] [IO.Directory]::CreateDirectory($outputDirectory)
$tools = (Resolve-Path -LiteralPath $SdkToolsDirectory).Path
$compliance = (Resolve-Path -LiteralPath $ComplianceDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $tools 'signtool.exe'))) { throw 'The SDK directory must contain signtool.exe.' }
foreach ($file in @(
    @{ Source = $PreviousSetupPath; Name = 'Previous-Setup.exe' },
    @{ Source = $SetupPath; Name = 'Setup.exe' },
    @{ Source = $StorePackagePath; Name = $storeInputName }
)) {
    Copy-Item -LiteralPath (Resolve-Path -LiteralPath $file.Source).Path -Destination (Join-Path $inputDirectory $file.Name)
}
function New-PayloadHashManifest([string] $Directory, [string] $ResourceIndex) {
    $payloadRoot = (Resolve-Path -LiteralPath $Directory).Path
    $requiredFiles = @('ClearCast.exe', 'ClearCast.dll', $ResourceIndex,
        'receiver\clearcast_receiver.dll', 'receiver\gst-plugin-scanner.exe',
        'receiver\libgstcodecparsers-1.0-0.dll', 'receiver\lib\gstreamer-1.0\libgstvideoparsersbad.dll',
        'receiver\gstreamer-security-manifest.json', 'receiver\security-dependencies-manifest.json')
    $dependencies = Get-Content -LiteralPath (Join-Path $payloadRoot 'receiver\security-dependencies-manifest.json') -Raw | ConvertFrom-Json
    foreach ($component in $dependencies.components) {
        foreach ($file in $component.files) {
            if ($file.relativePath -notin @('libplist-2.0.dll', 'libcrypto-3-x64.dll', 'libssl-3-x64.dll')) {
                throw 'Unexpected controlled dependency path in the sandbox payload.'
            }
            $requiredFiles += 'receiver\' + $file.relativePath
        }
    }
    $requiredFiles += @(foreach ($index in 0..79) { 'Assets\MirroringAnimation\frame-{0:D3}.svg' -f $index })
    if (@(Get-ChildItem -LiteralPath (Join-Path $payloadRoot 'Assets\MirroringAnimation') -Filter '*.svg' -File).Count -ne 80) {
        throw 'The published payload must contain exactly 80 SVG animation frames.'
    }
    foreach ($relative in $requiredFiles) {
        $file = Join-Path $payloadRoot $relative
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing published payload file: $file" }
    }
    # Check every shipped application/runtime file, including the HTTP/HLS,
    # audio and TLS dependency closure added since the older critical-file list.
    $sourceFiles = @{}
    foreach ($file in @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -File | Where-Object Name -ne 'ClearCast.pdb')) {
        $relative = [IO.Path]::GetRelativePath($payloadRoot, $file.FullName)
        if ($relative.Split('\')[0] -in @('licenses','THIRD-PARTY-COMPONENTS.md','runtime-components.json','source-coverage.json')) { continue }
        $sourceFiles[$relative] = $file.FullName
    }
    foreach ($file in @(Get-ChildItem -LiteralPath $compliance -Recurse -File)) {
        $sourceFiles[[IO.Path]::GetRelativePath($compliance, $file.FullName)] = $file.FullName
    }
    foreach ($relative in @($sourceFiles.Keys | Sort-Object)) {
        if ($relative.Split('\') -contains '.clearcast-state') { throw 'Private state cannot be an installation-validation input.' }
        $file = $sourceFiles[$relative]
        [pscustomobject]@{ relativePath = $relative; sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash }
    }
}
$desktopFiles = @(New-PayloadHashManifest $DesktopPublishDirectory 'ClearCast.pri')
$storeFiles = @(New-PayloadHashManifest $StorePublishDirectory 'resources.pri')
$appHash = ($desktopFiles | Where-Object relativePath -eq 'ClearCast.dll').sha256
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $inputDirectory $storeInputName))
try {
    $reader = [IO.StreamReader]::new($archive.GetEntry('AppxManifest.xml').Open())
    try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $entryMap = @{}
    foreach ($entry in $archive.Entries) {
        $name = [Uri]::UnescapeDataString($entry.FullName)
        if ($entryMap.ContainsKey($name) -or [IO.Path]::IsPathRooted($name) -or $name -match '(^|/)\.\.(/|$)') { throw 'The Store package has ambiguous or unsafe member names.' }
        $entryMap[$name] = $entry
    }
    foreach ($file in $storeFiles) {
        $entry = $entryMap[$file.relativePath.Replace('\', '/')]
        if (-not $entry) { throw "The Store MSIX is missing the published payload file: $($file.relativePath)" }
        $stream = $entry.Open()
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try { $entryHash = [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
        finally { $algorithm.Dispose(); $stream.Dispose() }
        if ($entryHash -ne $file.sha256) { throw "The Store MSIX does not match its published payload: $($file.relativePath)" }
    }
    $knownStorePaths = @($storeFiles.relativePath | ForEach-Object { $_.Replace('\','/') })
    foreach ($entry in $archive.Entries) {
        $name = [Uri]::UnescapeDataString($entry.FullName)
        if ($name.EndsWith('/') -or $name -in @('[Content_Types].xml','AppxBlockMap.xml','AppxSignature.p7x') -or $name -in $knownStorePaths) { continue }
        if ($name -notin @('AppxManifest.xml','Assets/StoreLogo.png','Assets/Square44x44Logo.png','Assets/Square150x150Logo.png')) { throw "Unexpected Store payload member: $name" }
        $stream = $entry.Open()
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try { $entryHash = [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
        finally { $algorithm.Dispose(); $stream.Dispose() }
        $storeFiles += [pscustomobject]@{ relativePath = $name.Replace('/','\'); sha256 = $entryHash }
    }
} finally { $archive.Dispose() }
foreach ($folder in @($DesktopPublishDirectory, $StorePublishDirectory)) {
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $folder 'ClearCast.dll')).FileVersion
    if ($version -ne [string]$manifest.Package.Identity.Version) {
        throw "Published application version $version does not match Store identity $($manifest.Package.Identity.Version)."
    }
}
@{
    schemaVersion = 3
    previousVersion = $PreviousVersion
    previousSetupSha256 = $PreviousSetupSha256.ToUpperInvariant()
    storePackageFileName = $storeInputName
    expectedSetupSha256 = (Get-FileHash -LiteralPath (Join-Path $inputDirectory 'Setup.exe')).Hash
    expectedStorePackageSha256 = (Get-FileHash -LiteralPath (Join-Path $inputDirectory $storeInputName)).Hash
    expectedDesktopAppSha256 = $appHash
    desktopFiles = $desktopFiles
    storeFiles = $storeFiles
    packageName = $manifest.Package.Identity.Name
    publisher = $manifest.Package.Identity.Publisher
    packageVersion = $manifest.Package.Identity.Version
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $inputDirectory 'configuration.json') -Encoding UTF8
$escape = { param($value) [Security.SecurityElement]::Escape($value) }
$inputXml = & $escape $inputDirectory
$outputXml = & $escape $outputDirectory
$scriptXml = & $escape $PSScriptRoot
$toolsXml = & $escape $tools
$configuration = @"
<Configuration>
  <vGPU>Disable</vGPU>
  <Networking>Disable</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <PrinterRedirection>Disable</PrinterRedirection>
  <MemoryInMB>4096</MemoryInMB>
  <MappedFolders>
    <MappedFolder><HostFolder>$inputXml</HostFolder><SandboxFolder>C:\ClearCastInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$scriptXml</HostFolder><SandboxFolder>C:\ClearCastScripts</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$toolsXml</HostFolder><SandboxFolder>C:\ClearCastTools</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$outputXml</HostFolder><SandboxFolder>C:\ClearCastOutput</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\ClearCastScripts\Test-InstalledPackages.ps1</Command></LogonCommand>
</Configuration>
"@
$configurationPath = Join-Path $work 'Validate-ClearCast.wsb'
$configuration | Set-Content -LiteralPath $configurationPath -Encoding UTF8
[pscustomobject]@{ ConfigurationPath = $configurationPath; WorkDirectory = $work; ResultPath = (Join-Path $outputDirectory 'results.json') } | ConvertTo-Json
