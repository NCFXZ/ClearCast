[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
# Never run installer/certificate/shutdown operations on the host computer.
if ($env:USERNAME -ne 'WDAGUtilityAccount' -or -not (Test-Path -LiteralPath 'C:\ClearCastInput\configuration.json')) {
    throw 'This script may run only through the generated Windows Sandbox configuration.'
}
$inputDirectory = 'C:\ClearCastInput'
$outputDirectory = 'C:\ClearCastOutput'
$workDirectory = 'C:\ClearCastValidation'
[void] [IO.Directory]::CreateDirectory($workDirectory)
$configuration = Get-Content -LiteralPath (Join-Path $inputDirectory 'configuration.json') -Raw | ConvertFrom-Json
$results = [ordered]@{ passed = $false; isolated = $true; network = 'disabled'; checks = @(); installationSnapshots = @(); cleanup = @(); error = $null }
$testCertificate = $null
$installedPackage = $null
$ownedProcesses = @()

# ARP hive locations are diagnostic only: the original dual-purpose installer can
# leave an HKLM ARP row even when Windows Installer records a per-user product.
# Query the Windows Installer engine for authoritative installation contexts.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class ClearCastInstalledProducts
{
    public sealed class Product
    {
        public string ProductCode;
        public string Name;
        public string UserSid;
        public uint Context;
        public string Scope;
    }
    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiEnumProductsEx(string productCode, string userSid, uint context,
        uint index, StringBuilder installedProductCode, out uint installedContext,
        StringBuilder sid, ref uint sidCharacters);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiEnumRelatedProducts(string upgradeCode, uint reserved,
        uint index, StringBuilder productCode);
    public static Product[] Enumerate()
    {
        var related = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (uint index = 0; ; index++)
        {
            var code = new StringBuilder(39);
            uint error = MsiEnumRelatedProducts("{6D6518A0-43F2-4C14-BD46-36C04820B3F1}", 0, index, code);
            if (error == 259) break;
            if (error != 0) throw new Win32Exception((int)error, "MsiEnumRelatedProducts failed.");
            related.Add(code.ToString());
        }
        var products = new List<Product>();
        for (uint index = 0; ; index++)
        {
            var code = new StringBuilder(39);
            var sid = new StringBuilder(256);
            uint sidCharacters = (uint)sid.Capacity;
            uint context;
            uint error = MsiEnumProductsEx(null, null, 7, index, code, out context, sid, ref sidCharacters);
            if (error == 259) break; // ERROR_NO_MORE_ITEMS
            if (error == 234)
            {
                sid = new StringBuilder(checked((int)sidCharacters + 1));
                sidCharacters = (uint)sid.Capacity;
                error = MsiEnumProductsEx(null, null, 7, index, code, out context, sid, ref sidCharacters);
            }
            if (error != 0) throw new Win32Exception((int)error, "MsiEnumProductsEx failed.");
            if (!related.Contains(code.ToString())) continue;
            products.Add(new Product { ProductCode = code.ToString(), Name = "ClearCast",
                UserSid = context == 4 ? null : sid.ToString(), Context = context,
                Scope = context == 4 ? "perMachine" : "perUser" });
        }
        return products.ToArray();
    }
}
'@

function Add-Check([string] $Name, $Detail) {
    $script:results.checks += [pscustomobject]@{ name = $Name; passed = $true; detail = $Detail }
}
function Check-InstalledPayload([string] $Directory, $Files, [string] $Name) {
    $payloadRoot = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
    $payloadPrefix = $payloadRoot + '\'
    if (@($Files).Count -lt 91 -or @($Files).Count -gt 20000) { throw "$Name requires the complete published-file validation manifest." }
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @($Files)) {
        $relative = [string]$file.relativePath
        if (-not $relative -or [IO.Path]::IsPathRooted($relative) -or -not $names.Add($relative) -or $file.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
            throw "$Name has an invalid validation manifest entry."
        }
        $path = [IO.Path]::GetFullPath((Join-Path $payloadRoot $relative))
        if (-not $path.StartsWith($payloadPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Name validation path escaped its installation directory."
        }
        $actualHash = if (Test-Path -LiteralPath $path -PathType Leaf) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } else { 'missing' }
        if ($actualHash -ne $file.sha256) {
            throw "$Name installed file does not match its published payload: $relative (expected $($file.sha256), actual $actualHash)"
        }
    }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payloadRoot 'ClearCast.dll')).FileVersion
    if ($version -ne $configuration.packageVersion) { throw "$Name application file version does not match the release." }
    $frameCount = @(Get-ChildItem -LiteralPath (Join-Path $payloadRoot 'Assets\MirroringAnimation') -Filter '*.svg' -File).Count
    if ($frameCount -ne 80) { throw "$Name must install exactly 80 SVG animation frames." }
    Add-Check $Name @{ directory = $payloadRoot; verifiedFileCount = @($Files).Count; animationFrameCount = $frameCount;
        applicationFileVersion = $version; receiverSha256 = ($Files | Where-Object relativePath -eq 'receiver\clearcast_receiver.dll').sha256;
        scannerSha256 = ($Files | Where-Object relativePath -eq 'receiver\gst-plugin-scanner.exe').sha256 }
}
function Run-Setup([string] $File, [string] $Operation, [string] $Log, [switch] $PermitRejected) {
    $arguments = @('/quiet', '/norestart', '/log', ('"' + (Join-Path $outputDirectory $Log) + '"'))
    if ($Operation -eq 'uninstall') { $arguments = @('/uninstall') + $arguments }
    elseif ($Operation -eq 'repair') { $arguments = @('/repair') + $arguments }
    $process = Start-Process -FilePath $File -ArgumentList $arguments -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(180000)) { $process.Kill(); throw "$Operation timed out." }
    if (-not $PermitRejected -and $process.ExitCode -notin @(0, 3010)) { throw "$Operation failed with exit code $($process.ExitCode)." }
    return $process.ExitCode
}
function Find-DesktopApp {
    foreach ($folder in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, (Join-Path $env:LOCALAPPDATA 'Programs'))) {
        if (-not $folder) { continue }
        $path = Join-Path $folder 'ClearCast\ClearCast.exe'
        if (Test-Path -LiteralPath $path) { return $path }
    }
    throw 'Installed ClearCast.exe was not found.'
}
function ClearCast-ArpRegistrations {
    $keys = @(
        @{ path = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall'; hive = 'HKLM'; view = '64' },
        @{ path = 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'; hive = 'HKLM'; view = '32' },
        @{ path = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'; hive = 'HKCU'; view = '64' },
        @{ path = 'HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'; hive = 'HKCU'; view = '32' }
    )
    return @(foreach ($key in $keys) {
        Get-ChildItem -LiteralPath $key.path -ErrorAction SilentlyContinue | ForEach-Object {
            $entry = Get-ItemProperty -LiteralPath $_.PSPath
            if ($entry.DisplayName -eq 'ClearCast') {
                [pscustomobject]@{
                    productCode = $_.PSChildName; hive = $key.hive; registryView = $key.view
                    registryPath = $_.Name; version = $entry.DisplayVersion
                    installLocation = $entry.InstallLocation; uninstallString = $entry.UninstallString
                    installSource = $entry.InstallSource; systemComponent = $entry.SystemComponent
                    windowsInstaller = $entry.WindowsInstaller; bundleUpgradeCode = $entry.BundleUpgradeCode
                    kind = $(if ($entry.WindowsInstaller -eq 1) { 'msi' } elseif ($entry.BundleUpgradeCode) { 'bundle' } else { 'other' })
                }
            }
        }
    })
}
function Record-InstallerSnapshot([string] $Stage) {
    $registrations = @(ClearCast-ArpRegistrations)
    $products = @([ClearCastInstalledProducts]::Enumerate())
    $snapshot = [pscustomobject]@{
        stage = $Stage; contextAuthority = 'MsiEnumProductsEx with MsiEnumRelatedProducts upgrade-family filter'
        msiProductCount = $products.Count; products = $products
        arpRegistrationCount = $registrations.Count; arpRegistrations = $registrations
    }
    $script:results.installationSnapshots += $snapshot
    return $snapshot
}
function Check-Application([Diagnostics.Process] $Process, [string] $Name) {
    $script:ownedProcesses += $Process
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 200
        $Process.Refresh()
        if ($Process.HasExited) { throw "$Name exited during startup (code $($Process.ExitCode))." }
    } while ($Process.MainWindowHandle -eq 0 -and $watch.Elapsed.TotalSeconds -lt 30)
    if ($Process.MainWindowHandle -eq 0) { throw "$Name did not create a window." }
    $startup = [math]::Round($watch.Elapsed.TotalMilliseconds)
    Start-Sleep -Seconds 3
    $Process.Refresh()
    if ($Process.HasExited -or -not $Process.Responding) { throw "$Name became unresponsive." }
    Add-Check $Name @{ startupMilliseconds = $startup; workingSetBytes = $Process.WorkingSet64; privateBytes = $Process.PrivateMemorySize64; windowTitle = $Process.MainWindowTitle; receiverNetworkTest = 'not run: Sandbox networking is disabled' }
    [void] $Process.CloseMainWindow()
    if (-not $Process.WaitForExit(15000)) { throw "$Name did not close within 15 seconds." }
}

try {
    if ($configuration.schemaVersion -ne 3) { throw 'The Sandbox validation configuration requires schema version 3.' }
    if (Get-Process ClearCast -ErrorAction SilentlyContinue) { throw 'The disposable VM already has an unexpected ClearCast process.' }
    $previousSetup = Join-Path $inputDirectory 'Previous-Setup.exe'
    $setup = Join-Path $inputDirectory 'Setup.exe'
    if ((Get-FileHash -LiteralPath $setup).Hash -ne $configuration.expectedSetupSha256 -or
        (Get-FileHash -LiteralPath (Join-Path $inputDirectory $configuration.storePackageFileName)).Hash -ne $configuration.expectedStorePackageSha256) {
        throw 'A final release artifact changed after Sandbox configuration generation.'
    }
    if ((Get-FileHash -LiteralPath $previousSetup -Algorithm SHA256).Hash -ne $configuration.previousSetupSha256) {
        throw 'The previous installer changed after the public-release provenance check.'
    }
    $exitCode = Run-Setup $previousSetup 'install' 'install-previous.log'
    $previousSnapshot = Record-InstallerSnapshot 'Previous installer default installation'
    if ($previousSnapshot.msiProductCount -ne 1) { throw 'Previous public installer did not create exactly one Windows Installer product.' }
    $previousApp = Find-DesktopApp
    $previousVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo([IO.Path]::ChangeExtension($previousApp, '.dll')).FileVersion
    if ($previousVersion -ne $configuration.previousVersion) { throw 'The previous installed application version does not match the requested upgrade baseline.' }
    $previousHash = (Get-FileHash -LiteralPath ([IO.Path]::ChangeExtension($previousApp, '.dll')) -Algorithm SHA256).Hash
    Add-Check ('Install previous ' + $configuration.previousVersion) @{ exitCode = $exitCode; app = $previousApp; sha256 = $previousHash; fileVersion = $previousVersion; setupSha256 = $configuration.previousSetupSha256 }

    $exitCode = Run-Setup $setup 'install' 'upgrade-version.log'
    $snapshot = Record-InstallerSnapshot 'Release version upgrade'
    $app = Find-DesktopApp
    $appHash = (Get-FileHash -LiteralPath ([IO.Path]::ChangeExtension($app, '.dll')) -Algorithm SHA256).Hash
    if ($appHash -ne $configuration.expectedDesktopAppSha256) { throw 'Version upgrade did not install the exact new application assembly.' }
    if ($appHash -eq $previousHash) { throw 'Previous and replacement application hashes unexpectedly match.' }
    if ($snapshot.msiProductCount -ne 1) { throw 'Version upgrade left a missing or duplicate Windows Installer product.' }
    if ($snapshot.products[0].Scope -ne 'perUser') { throw 'Version upgrade did not install in the per-user Windows Installer context.' }
    $msiArp = @($snapshot.arpRegistrations | Where-Object kind -eq 'msi')
    $visibleBundles = @($snapshot.arpRegistrations | Where-Object { $_.kind -eq 'bundle' -and $_.systemComponent -ne 1 })
    if ($msiArp.Count -ne 1 -or $msiArp[0].productCode -ne $snapshot.products[0].ProductCode -or $visibleBundles.Count -ne 1 -or $snapshot.arpRegistrationCount -ne 2) { throw 'Version upgrade left a missing, duplicate or stale MSI/bundle ARP registration.' }
    if ($msiArp[0].version -ne ([Version]$configuration.packageVersion).ToString(3)) { throw 'The installed MSI version does not match the release.' }
    Check-InstalledPayload (Split-Path -Parent $app) $configuration.desktopFiles 'Installed desktop payload hashes and version'
    Add-Check ('Upgrade previous ' + $configuration.previousVersion + ' to ' + $configuration.packageVersion) @{ exitCode = $exitCode; app = $app; sha256 = $appHash; msiProductCount = $snapshot.msiProductCount; scope = $snapshot.products[0].Scope; context = $snapshot.products[0].Context; arpRegistrationCount = $snapshot.arpRegistrationCount }
    $downgradeCode = Run-Setup $previousSetup 'install' 'downgrade-rejected.log' -PermitRejected
    $afterDowngrade = Record-InstallerSnapshot 'Previous installer cannot replace the new release'
    if ($afterDowngrade.msiProductCount -ne 1 -or $afterDowngrade.products[0].ProductCode -ne $snapshot.products[0].ProductCode) {
        throw 'A previous-version installer replaced or duplicated the current product.'
    }
    Check-InstalledPayload (Split-Path -Parent $app) $configuration.desktopFiles 'Previous installer leaves the new payload intact'
    Add-Check 'Previous-version downgrade rejected or left unchanged' @{ exitCode = $downgradeCode; currentProductCode = $snapshot.products[0].ProductCode }
    $repairCode = Run-Setup $setup 'repair' 'repair-current.log'
    $afterRepair = Record-InstallerSnapshot 'Current version repair'
    if ($afterRepair.msiProductCount -ne 1 -or $afterRepair.arpRegistrationCount -ne 2) { throw 'Repair changed the expected product registrations.' }
    Check-InstalledPayload (Split-Path -Parent $app) $configuration.desktopFiles 'Repaired desktop payload hashes and version'
    Add-Check 'Current version repair' @{ exitCode = $repairCode; msiProductCount = $afterRepair.msiProductCount }
    # A visible window is required for the UI startup/close smoke test inside this VM.
    Check-Application (Start-Process -FilePath $app -PassThru -WindowStyle Normal) 'Installed desktop application startup and close'
    $exitCode = Run-Setup $setup 'uninstall' 'uninstall-desktop.log'
    $remaining = Record-InstallerSnapshot 'Replacement uninstall'
    if ((Test-Path -LiteralPath $app) -or $remaining.msiProductCount -ne 0 -or $remaining.arpRegistrationCount -ne 0) { throw 'Desktop uninstall left the executable, Windows Installer product or ARP registration.' }
    Add-Check 'Desktop uninstall' @{ exitCode = $exitCode; executableRemoved = $true; msiRegistrationRemoved = $true }

    $freshCode = Run-Setup $setup 'install' 'install-clean-current.log'
    $fresh = Record-InstallerSnapshot 'Clean current-version installation'
    $app = Find-DesktopApp
    if ($fresh.msiProductCount -ne 1 -or $fresh.products[0].Scope -ne 'perUser' -or $fresh.arpRegistrationCount -ne 2) { throw 'A clean installation has an unexpected scope or product registration.' }
    Check-InstalledPayload (Split-Path -Parent $app) $configuration.desktopFiles 'Clean desktop payload hashes and version'
    Add-Check 'Clean current-version installation' @{ exitCode = $freshCode; msiProductCount = $fresh.msiProductCount }
    Check-Application (Start-Process -FilePath $app -PassThru -WindowStyle Normal) 'Clean desktop application startup and close'
    $cleanUninstall = Run-Setup $setup 'uninstall' 'uninstall-clean-current.log'
    $cleanRemaining = Record-InstallerSnapshot 'Clean installation uninstall'
    if ((Test-Path -LiteralPath $app) -or $cleanRemaining.msiProductCount -ne 0 -or $cleanRemaining.arpRegistrationCount -ne 0) { throw 'Clean-install uninstall left the application or registrations.' }
    Add-Check 'Clean installation uninstall' @{ exitCode = $cleanUninstall; registrationRemoved = $true }

    if ($configuration.storePackageFileName -notin @('Store.appx', 'Store.msix')) { throw 'Unexpected Store validation package filename.' }
    $unsigned = Join-Path $inputDirectory $configuration.storePackageFileName
    $signed = Join-Path $workDirectory ('Store-test-signed' + [IO.Path]::GetExtension($configuration.storePackageFileName))
    Copy-Item -LiteralPath $unsigned -Destination $signed
    $testCertificate = New-SelfSignedCertificate -Type Custom -Subject $configuration.publisher -KeyUsage DigitalSignature -FriendlyName 'ClearCast disposable Sandbox test' -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -KeyExportPolicy Exportable -NotAfter (Get-Date).AddDays(1) -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    $passwordText = [Guid]::NewGuid().ToString('N')
    $password = ConvertTo-SecureString -String $passwordText -Force -AsPlainText
    $pfx = Join-Path $workDirectory 'sandbox-test.pfx'
    $cer = Join-Path $workDirectory 'sandbox-test.cer'
    [void] (Export-PfxCertificate -Cert $testCertificate -FilePath $pfx -Password $password)
    [void] (Export-Certificate -Cert $testCertificate -FilePath $cer)
    [void] (Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople)
    & C:\ClearCastTools\signtool.exe sign /fd SHA256 /f $pfx /p $passwordText $signed *> (Join-Path $outputDirectory 'msix-sign.log')
    if ($LASTEXITCODE -ne 0) { throw 'Sandbox test MSIX signing failed.' }
    $passwordText = $null
    & C:\ClearCastTools\signtool.exe verify /pa $signed *> (Join-Path $outputDirectory 'msix-signature-verify.log')
    if ($LASTEXITCODE -ne 0) { throw 'Sandbox MSIX signature verification failed.' }
    Add-AppxPackage -Path $signed
    $installedPackage = Get-AppxPackage -Name $configuration.packageName
    if (-not $installedPackage -or $installedPackage.Version.ToString() -ne $configuration.packageVersion -or $installedPackage.Publisher -ne $configuration.publisher) { throw 'MSIX package identity/version does not match the manifest.' }
    Check-InstalledPayload $installedPackage.InstallLocation $configuration.storeFiles 'Installed MSIX payload hashes and version'
    Add-Check 'MSIX isolated signing and installation' @{ name = $installedPackage.Name; version = $installedPackage.Version.ToString(); publisher = $installedPackage.Publisher; testCertificateScope = 'Sandbox only: user signing key and VM local-machine public trust' }
    Start-Process -FilePath explorer.exe -ArgumentList ('shell:AppsFolder\' + $installedPackage.PackageFamilyName + '!ClearCast')
    $limit = (Get-Date).AddSeconds(30)
    do { Start-Sleep -Milliseconds 200; $storeApp = Get-Process ClearCast -ErrorAction SilentlyContinue | Select-Object -First 1 } while (-not $storeApp -and (Get-Date) -lt $limit)
    if (-not $storeApp) { throw 'Installed MSIX did not launch.' }
    Check-Application $storeApp 'Installed MSIX startup and close'
    Remove-AppxPackage -Package $installedPackage.PackageFullName
    if (Get-AppxPackage -Name $configuration.packageName) { throw 'MSIX uninstall left a registered package.' }
    $installedPackage = $null
    Add-Check 'MSIX uninstall' @{ registrationRemoved = $true }
    $results.passed = $true
} catch {
    $results.error = $_.Exception.Message
    $results.checks += [pscustomobject]@{ name = 'Package validation'; passed = $false; detail = $_.Exception.Message }
} finally {
    try {
    foreach ($process in $ownedProcesses) {
        if (-not $process.HasExited) { [void] $process.CloseMainWindow(); if (-not $process.WaitForExit(5000)) { $process.Kill() } }
    }
    try {
        Get-AppxPackage -Name $configuration.packageName | Remove-AppxPackage
        $results.cleanup += 'Sandbox test MSIX registration removed'
    } catch { $results.passed = $false; $results.cleanup += ('MSIX cleanup failed: ' + $_.Exception.Message) }
    foreach ($entry in @(ClearCast-ArpRegistrations | Where-Object kind -eq 'msi')) {
        if ($entry.productCode -match '^\{[0-9A-Fa-f-]{36}\}$') {
            $process = Start-Process -FilePath msiexec.exe -ArgumentList @('/x', $entry.productCode, '/quiet', '/norestart') -WindowStyle Hidden -PassThru
            if (-not $process.WaitForExit(120000)) { $process.Kill(); $results.passed = $false }
        }
    }
    if ($testCertificate) {
        foreach ($store in @('Cert:\CurrentUser\My', 'Cert:\LocalMachine\TrustedPeople')) {
            $path = "$store\$($testCertificate.Thumbprint)"
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
        }
        $results.cleanup += 'Sandbox-only test certificate and trust removed'
    }
    $resolvedWork = [IO.Path]::GetFullPath($workDirectory)
    if ($resolvedWork -ne 'C:\ClearCastValidation') { throw 'Unexpected Sandbox cleanup target.' }
    Remove-Item -LiteralPath $resolvedWork -Recurse -Force
    $results.cleanup += 'Sandbox working directory and private signing key removed'
    } catch {
        $results.passed = $false
        $results.cleanup += ('Cleanup failed before VM disposal: ' + $_.Exception.Message)
    }
    $pendingResult = Join-Path $outputDirectory 'results.pending.json'
    $results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $pendingResult -Encoding UTF8
    Move-Item -LiteralPath $pendingResult -Destination (Join-Path $outputDirectory 'results.json') -Force
    # Powering off a Windows Sandbox discards its VM, installed files and user data.
    & shutdown.exe /s /t 0
}
