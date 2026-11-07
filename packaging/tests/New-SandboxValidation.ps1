[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PreviousSetupPath,
    [Parameter(Mandatory)] [string] $SetupPath,
    [Parameter(Mandatory)] [string] $StorePackagePath,
    [Parameter(Mandatory)] [string] $DesktopPublishDirectory,
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
$appHash = (Get-FileHash -LiteralPath (Join-Path $DesktopPublishDirectory 'ClearCast.dll') -Algorithm SHA256).Hash
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $inputDirectory 'Store.msix'))
try {
    $reader = [IO.StreamReader]::new($archive.GetEntry('AppxManifest.xml').Open())
    try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
} finally { $archive.Dispose() }
@{
    expectedDesktopAppSha256 = $appHash
    packageName = $manifest.Package.Identity.Name
    publisher = $manifest.Package.Identity.Publisher
    packageVersion = $manifest.Package.Identity.Version
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $inputDirectory 'configuration.json') -Encoding UTF8
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
