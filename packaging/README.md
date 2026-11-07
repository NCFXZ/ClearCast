# Windows release packages

Build the native receiver as described in the project README, then publish both WinUI layouts from the repository root.

```powershell
dotnet publish ClearCast.csproj -c Release -r win-x64 -o .runtime/release/desktop -p:Version=1.1.0
dotnet publish ClearCast.csproj -c Release -r win-x64 -o .runtime/release/store -p:Version=1.1.0 `
  -p:BaseIntermediateOutputPath=.runtime/release/store-obj/ -p:BaseOutputPath=.runtime/release/store-bin/ `
  -p:ProjectPriFileName=resources.pri -p:ProjectPriIndexName=NCFX.ClearCast
```

Use WiX 5.0.2, its `WixToolset.Bal.wixext` extension, and the Windows SDK MakeAppx tool. Pass the binary distribution notices and `licenses` folder for the exact published dependencies as `-ComplianceDirectory`.

```powershell
./packaging/Build-Packages.ps1 -PublishDirectory .runtime/release/desktop `
  -StorePublishDirectory .runtime/release/store -ComplianceDirectory $notices `
  -OutputDirectory .runtime/release/packages -WixPath $wix -WixBalExtensionPath $bal -MakeAppxPath $makeappx `
  -ValidatedReceiverSha256 $testedReceiverSha256
```

The script produces a self-contained EXE installer and an unsigned Store MSIX. Partner Center signs the MSIX during certification.

For a Store-only metadata update, use the same validated publish folders and append `-StoreOnly`. This rebuilds the MSIX from the current manifest without replacing an already tested EXE installer. A display-name change does not require recompiling unchanged application code; verify the new MSIX identity, file hashes and installation again.

Before assembling a package, it requires the privacy policy and controlled parser manifest, checks both parser DLL hashes, and matches them against the exact distribution notices. To run only these read-only checks, append `-ValidateOnly` to the same command; this does not create an installer, MSIX, or staging directory. The final ClearCast receiver DLL hash must also match the separately validated release candidate.

The EXE installs for the current user in `%LOCALAPPDATA%\Programs\ClearCast`, matching the original public installer's default context and directory. It does not require machine-wide installation privileges. Its MSI permits a corrected build with the same three-part release version to replace the previous build; the UpgradeCode stays unchanged. WiX 5 Burn cannot safely manage a dual-purpose MSI whose context changes, so this package explicitly uses `perUser`. Manually forcing a machine-wide installation of an extracted previous MSI is outside this installer upgrade path. New installations and major upgrades set REINSTALLMODE=amus before costing: this ensures that an intentionally selected, lower-numbered supported dependency branch replaces the previous binary exactly. Existing-product repair keeps the default policy. Removing the previous product after InstallInitialize keeps its removal within the installer transaction.

## Corresponding source

After committing the release, create the application source with `git archive --format=zip --prefix=ClearCast/`. Refresh the large dependency-source bundle with:

```powershell
python packaging/Update-CorrespondingSource.py --base $previousSourceBundle `
  --app-source $applicationSourceZip --commit $commitSha --version 1.1.0 --output $updatedSourceBundle `
  --compliance-dir $notices
python packaging/tests/test_source_bundle.py
```

This verifies the base source checksums, replaces the application archive and commit/hash metadata, and extracts its distribution notices. Reuse dependency sources and notices only when they still correspond to the shipped binaries.

For the controlled GStreamer parser update, prepare notices from its source archive and build manifest first:

```powershell
python packaging/Prepare-DependencyNotices.py --base $previousSourceBundle `
  --gstreamer-source $gstreamerSourceTar --security-directory .runtime/gstreamer-security `
  --output-directory $updatedNotices
python packaging/Update-CorrespondingSource.py --base $previousSourceBundle `
  --app-source $applicationSourceZip --commit $commitSha --version 1.1.0 --output $updatedSourceBundle `
  --notice-directory $updatedNotices `
  --additional-source "gstreamer/gst-plugins-bad-1.28.9.tar.xz=$gstreamerSourceTar"
```

Use `$updatedNotices` as the package build's `ComplianceDirectory`. This retains sources for unchanged MSYS2 components while adding the official 1.28.9 parser source, license, exact binary hashes and controlled build configuration. The application source archive contains the matching build script. The updater rejects generated/private state and conflicting archive paths, and hashes the effective notices as well as corresponding source archives.

## Disposable installation checks

On a machine with Windows Sandbox already enabled, generate a configuration using the previous installer, replacement installer, unsigned Store package, final desktop publish folder, and the Windows SDK x64 tools directory:

```powershell
packaging/tests/New-SandboxValidation.ps1 -PreviousSetupPath $previousSetup `
  -SetupPath $setup -StorePackagePath $msix -DesktopPublishDirectory $desktop -StorePublishDirectory $storePublish `
  -SdkToolsDirectory $sdkTools
```

Launch the returned `.wsb` file and wait for its `ResultPath`. The inputs, test scripts, and SDK tools are mapped read-only; only a new result directory is writable. Networking, clipboard, microphone, camera, and printer access are disabled. The script installs the previous 1.0.0 using its default options, upgrades it to 1.1.0, checks the exact application hash and exactly one per-user MSI registration, verifies the installed version and 91 critical file hashes (including all 80 SVG frames, both parser components, the receiver, scanner, updated property-list and cryptography libraries), opens and closes it, and uninstalls it. Registration snapshots record the hive, product code, installation location and uninstall command before assertions, including on failures. It signs a copy of the MSIX using a short-lived certificate trusted only inside the VM, checks installation, the separate Store build's 91 critical file hashes, launch and removal, then removes the test certificate and private key and powers off the disposable VM.

`Test-InstalledPackages.ps1` refuses to run on the host. Sandbox results cover installation and application startup/close; disabled networking means they do not validate real-device mirroring. Delete the generated host-side input/output directory after retaining the final validation results.

## Package preflight regression

Run `packaging/tests/Test-PackagePreflight.ps1` with the two published directories, compliance directory, tested receiver hash and an output report path. It checks the unchanged payload, then independently rejects a wrong version, untested receiver, missing or mismatched scanner, missing or extra SVG frames, tampered property-list and cryptography libraries, and tampered parser binaries or notices. Each case modifies a disposable fixture; the release payload stays untouched.
