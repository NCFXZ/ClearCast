[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PreviousSetupPath,
    [Parameter(Mandatory)] [string] $SetupPath,
    [Parameter(Mandatory)] [string] $StorePackagePath,
    [Parameter(Mandatory)] [string] $DesktopPublishDirectory,
    [Parameter(Mandatory)] [string] $StorePublishDirectory,
    [Parameter(Mandatory)] [string] $SdkToolsDirectory
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$work = Join-Path $repository ('.runtime\sandbox-validation-' + [Guid]::NewGuid().ToString('N'))
$inputDirectory = Join-Path $work 'input'
$outputDirectory = Join-Path $work 'output'
[void] [IO.Directory]::CreateDirectory($inputDirectory)
[void] [IO.Directory]::CreateDirectory($outputDirectory)
$tools = (Resolve-Path -LiteralPath $SdkToolsDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $tools 'signtool.exe'))) { throw 'The SDK directory must contain signtool.exe.' }
foreach ($file in @(
    @{ Source = $PreviousSetupPath; Name = 'Previous-Setup.exe' },
    @{ Source = $SetupPath; Name = 'Setup.exe' },
    @{ Source = $StorePackagePath; Name = 'Store.msix' }
)) {
    Copy-Item -LiteralPath (Resolve-Path -LiteralPath $file.Source).Path -Destination (Join-Path $inputDirectory $file.Name)
}
function New-PayloadHashManifest([string] $Directory, [string] $ResourceIndex) {
    $payloadRoot = (Resolve-Path -LiteralPath $Directory).Path
    $relativeFiles = @('ClearCast.exe', 'ClearCast.dll', $ResourceIndex,
        'receiver\clearcast_receiver.dll', 'receiver\gst-plugin-scanner.exe',
        'receiver\libgstcodecparsers-1.0-0.dll', 'receiver\lib\gstreamer-1.0\libgstvideoparsersbad.dll',
        'receiver\gstreamer-security-manifest.json', 'receiver\security-dependencies-manifest.json')
    $dependencies = Get-Content -LiteralPath (Join-Path $payloadRoot 'receiver\security-dependencies-manifest.json') -Raw | ConvertFrom-Json
    foreach ($component in $dependencies.components) {
        foreach ($file in $component.files) {
            if ($file.relativePath -notin @('libplist-2.0.dll', 'libcrypto-3-x64.dll', 'libssl-3-x64.dll')) {
                throw 'Unexpected controlled dependency path in the sandbox payload.'
            }
            $relativeFiles += 'receiver\' + $file.relativePath
        }
    }
    $relativeFiles += @(foreach ($index in 0..79) { 'Assets\MirroringAnimation\frame-{0:D3}.svg' -f $index })
    if (@(Get-ChildItem -LiteralPath (Join-Path $payloadRoot 'Assets\MirroringAnimation') -Filter '*.svg' -File).Count -ne 80) {
        throw 'The published payload must contain exactly 80 SVG animation frames.'
    }
    foreach ($relative in $relativeFiles) {
        $file = Join-Path $payloadRoot $relative
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing published payload file: $file" }
        [pscustomobject]@{ relativePath = $relative; sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash }
    }
}
$desktopFiles = @(New-PayloadHashManifest $DesktopPublishDirectory 'ClearCast.pri')
$storeFiles = @(New-PayloadHashManifest $StorePublishDirectory 'resources.pri')
$appHash = ($desktopFiles | Where-Object relativePath -eq 'ClearCast.dll').sha256
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $inputDirectory 'Store.msix'))
try {
    $reader = [IO.StreamReader]::new($archive.GetEntry('AppxManifest.xml').Open())
    try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    foreach ($file in $storeFiles) {
        $entry = $archive.GetEntry($file.relativePath.Replace('\', '/'))
        if (-not $entry) { throw "The Store MSIX is missing the published payload file: $($file.relativePath)" }
        $stream = $entry.Open()
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try { $entryHash = [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
        finally { $algorithm.Dispose(); $stream.Dispose() }
        if ($entryHash -ne $file.sha256) { throw "The Store MSIX does not match its published payload: $($file.relativePath)" }
    }
} finally { $archive.Dispose() }
foreach ($folder in @($DesktopPublishDirectory, $StorePublishDirectory)) {
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $folder 'ClearCast.dll')).FileVersion
    if ($version -ne [string]$manifest.Package.Identity.Version) {
        throw "Published application version $version does not match Store identity $($manifest.Package.Identity.Version)."
    }
}
@{
    schemaVersion = 2
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
