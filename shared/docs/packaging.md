# Windows release packages

Run commands from the repository root. Application sources live in `src/ClearCast/`, native sources and patches in `src/native/`, scripts in `scripts/packaging/`, templates and Store metadata in `packaging/`, and disposable outputs in `.runtime/`.

## Version and publish layouts

The release, application/file version, WiX Bundle and Store identity use `1.2.2.0`. MSI ProductVersion uses `1.2.2`: [Windows Installer compares three version fields](https://learn.microsoft.com/en-us/windows/win32/msi/productversion), and [Store packages reserve the fourth field as zero](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/package-version-numbering). `Build-Packages.ps1` accepts either spelling, normalizes it to those forms, and rejects a nonzero fourth field or MSI field overflow. Increment an MSI field for an upgrade. Preserve the existing MSI/Bundle UpgradeCodes and Store Name/Publisher.

Build the tested native receiver first, then publish separate WinUI layouts:

```powershell
$repository = (Get-Location).Path
$release = '.runtime/release-1.2.2.0'
dotnet publish src/ClearCast/ClearCast.csproj -c Release -r win-x64 -o "$release/desktop" -p:Version=1.2.2.0
dotnet publish src/ClearCast/ClearCast.csproj -c Release -r win-x64 -o "$release/store" -p:Version=1.2.2.0 `
  -p:BaseIntermediateOutputPath="$repository/$release/store-obj/" -p:BaseOutputPath="$repository/$release/store-bin/" `
  -p:ProjectPriFileName=resources.pri -p:ProjectPriIndexName=NCFX.ClearCast
```

The desktop PRI is `ClearCast.pri`; Store uses `resources.pri` with index `NCFX.ClearCast`. Disable test-only compilation switches in both distributable layouts. The brand source is `src/ClearCast/Assets/ClearCast.png`; verify shipped PNG/ICO and PRI resources against the matching source/derived icon.

## Exact runtime notices

Authenticate the previous corresponding-source bundle against its published SHA-256, then prepare complete notices against the exact receiver and installed signed MSYS2 package database:

```powershell
python scripts/packaging/Prepare-RuntimeNotices.py --base $previousSourceBundle `
  --base-sha256 $previousSourceSha256 --msys-root .runtime/msys64 `
  --runtime-directory .runtime/receiver --additional-source-directory $officialMsysSourceDirectory `
  --output-directory $notices --version 1.2.2.0
```

The output must be a new directory. If the candidate runtime is under a QA subdirectory, pass `--security-component-directory .runtime` to identify the existing controlled build manifests explicitly. An optional `--package-audit` supplies an independent owner audit; otherwise the tool audits the installed database. Each native PE and CA file must match its package owner or controlled manifest. New sources must match the package/version and the signed binary package's BUILDINFO recipe digest. Fetch exact archives from the official MSYS2 source repository while preserving TLS and package signature checks. Separately check embedded upstream archives/patches against recipe checksums; `SKIP` signature items require their stated provenance treatment.

The output includes `THIRD-PARTY-COMPONENTS.md`, `runtime-components.json`, `source-coverage.json`, and per-package licenses/source-build records. It covers HTTP/HLS/TLS and CA sources as well as controlled libplist, OpenSSL, parser and HTTP-source builds. Replacing selected controlled notices is insufficient when other MSYS2 packages changed. `Prepare-DependencyNotices.py` remains for historical two-parser baseline migrations; current releases use the complete runtime gate and three-file parser/adaptive component manifest.

Build both `scripts/native/Build-GStreamerSecurity.ps1` and `scripts/native/Build-GStreamerHttpSecurity.ps1` before the receiver. The parser/adaptive manifest binds three shipped files: `libgstcodecparsers-1.0-0.dll`, `lib/gstreamer-1.0/libgstvideoparsersbad.dll` and `libgstadaptivedemux-1.0-0.dll`. Its official gst-plugins-bad 1.28.9 source SHA-256 is `4A4E0B7BF84F868378C48F143A7AC3A98923B488A5B74F1A35BA46BC3C43F6B1`; `sourcePatch={relativePath,sha256}` binds `src/native/patches/gstreamer-adaptive-demux-time.patch`, and `buildRecipeSha256` identifies the build script used. Runtime notices include the original COPYING, exact patch and source-build record; `source-coverage.json` binds the exact source archive through `parserSource` and any corresponding additional source.

Both publish layouts must also carry `receiver/gstreamer-http-security-manifest.json`, `receiver/LICENSE-GStreamerHttpSecurity.txt` and its exact `lib/gstreamer-1.0/libgstsoup.dll`. The HTTP manifest binds official gst-plugins-good 1.28.9 source SHA-256 `E518CD97483706A252E95E60312A22D23DEC6F5E7673AF6F5BDE719C3A9CA7F4` and `sourcePatch={relativePath,sha256}` for `src/native/patches/gstreamer-soup-destination.patch`. Notices include its source-build record, original COPYING and exact patch. Put each official archive in the additional-source directory (or the controlled component parent); coverage records `gstreamer/gst-plugins-good-1.28.9.tar.xz` and `gstreamer/gst-plugins-bad-1.28.9.tar.xz`, either from the authenticated baseline or as an additional source. All three parser/adaptive binary checks and the separate HTTP-source check remain mandatory.

License reads require canonical members, exact regular files under the expected package root, and literal tar arguments after `--`; content bytes are preserved. Generated/private pairing state is excluded. `TEST-ONLY` compliance fixtures cannot produce release packages.

## Build packages

Use WiX 5.0.2, its matching `WixToolset.Bal.wixext` extension, and Windows SDK x64 MakeAppx. Keep official tools/packages under `.runtime/` and pass the independently tested receiver SHA-256:

```powershell
./scripts/packaging/Build-Packages.ps1 -PublishDirectory "$release/desktop" `
  -StorePublishDirectory "$release/store" -ComplianceDirectory $notices `
  -OutputDirectory "$release/packages" -Version 1.2.2.0 `
  -WixPath $wix -WixBalExtensionPath $bal -MakeAppxPath $makeappx `
  -ValidatedReceiverSha256 $testedReceiverSha256 -AlsoBuildMsix
```

Defaults produce a self-contained EXE and unsigned Store APPX. `-AlsoBuildMsix` invokes MakeAppx separately on the same staging tree; `-StoreFormat msix` selects MSIX as primary. Compare every uncompressed entry and identity when producing both; outer ZIP hashes may differ. Partner Center signs the submitted container. Do not rename an extension to imitate another format.

`-ValidateOnly` checks inputs without creating staging/output. Checks bind version/receiver hash, controlled binaries, scanner, legal files, 200 SVG frames and complete native inventory to both layouts. Inputs also reject private/generated state directories and reparse points; assemblies marked `+ui-tests` cannot be packaged. Source coverage binds the inventory text SHA, total/PE/package counts, additional source archives and matching package records. `-PreflightFixture` permits disposable subset tests only with `-ValidateOnly`; it cannot create packages. `-StoreOnly` rebuilds the selected Store format without replacing the EXE.

Staging is recreated from current payload and exact notices; stale generated notice trees are excluded before the current tree is copied. WiX 5 cabinet input paths must be shorter than 260 characters; the builder rejects longer paths before invocation. Keep output roots/license paths short rather than altering system settings. The failure is described in the matching [WiX issue](https://github.com/wixtoolset/issues/issues/9115).

The EXE installs per user in `%LOCALAPPDATA%\Programs\ClearCast`, matching the public 1.1.0 installer. Preserve the per-user context and upgrade family. Same-version corrected MSI builds may replace predecessors. New install/major upgrade sets `REINSTALLMODE=amus` before costing so deliberately selected supported dependency branches replace old binaries exactly; existing-product repair retains default policy. Old-product removal follows InstallInitialize inside the upgrade transaction. A manually forced machine-wide extracted MSI is outside this path.

## Corresponding source

After the final commit, create application source with `git archive --format=zip --prefix=ClearCast/`. Refresh the authenticated dependency baseline with exact notices and every new source from coverage:

```powershell
$coverage = Get-Content "$notices/source-coverage.json" -Raw | ConvertFrom-Json
$additional = @()
foreach ($entry in $coverage.additionalSources.PSObject.Properties) {
  $additional += '--additional-source'
  $additional += "$($entry.Name)=$($entry.Value.path)"
}
python scripts/packaging/Update-CorrespondingSource.py --base $previousSourceBundle `
  --app-source $applicationSourceZip --commit $commitSha --version 1.2.2.0 `
  --output $updatedSourceBundle --notice-directory $notices @additional
```

The updater verifies base checksums, replaces app source/commit/hash metadata and effective notices, adds exact source archives and refreshes checksums. It rejects noncanonical paths, duplicate/case-alias members, reserved-name conflicts and generated/private state. Retain old dependency archives only when they correspond to shipped files. Verify the resulting archive/notices before upload and bind app source to the actual release commit.

## Disposable installation checks

On a host with Windows Sandbox available, use the SHA-verified public 1.2.0.0 EXE, replacement EXE, actual APPX, final layouts, complete notices and SDK x64 tools:

```powershell
tests/packaging/New-SandboxValidation.ps1 -PreviousSetupPath $previousSetup `
  -SetupPath $setup -StorePackagePath $appx -DesktopPublishDirectory "$release/desktop" `
  -StorePublishDirectory "$release/store" -ComplianceDirectory $notices -SdkToolsDirectory $sdkTools
```

Pass `-PreviousVersion 1.2.0.0` and its independently verified `-PreviousSetupSha256` for this release. The helper also retains its pinned 1.1.0.0 default for older upgrade checks; both host and guest verify the selected baseline. Schema 3 binds new EXE/Store outer hashes and every expected shipped file, including notices/runtime/PRI/Store-generated assets. MakeAppx member names are URI-decoded once to compare installed filenames.

Launch the returned `.wsb` and wait for its result. Inputs/scripts/tools are mapped read-only; a new result directory is writable. Networking, clipboard, microphone, camera and printer access are disabled. The guest checks old installation/version, upgrade, one per-user MSI registration, downgrade rejection, repair, launch/close/uninstall and clean current install. It signs only a guest APPX copy using a temporary guest-trusted certificate, checks Store installation/hashes/launch/removal, removes certificate/private key and shuts down. Registration snapshots remain available on failure.

`Test-InstalledPackages.ps1` refuses to run on the host. Guest checks cover installation and startup, not real-device AirPlay or physical GPU/audio compatibility. Retain results before removing disposable host input/output directories.

## Regression and cleanup

```powershell
python tests/packaging/test_source_bundle.py
python tests/packaging/test_runtime_notices.py
python tests/packaging/test_github_release.py
tests/packaging/Test-PackagePreflight.ps1 -PublishDirectory "$release/desktop" `
  -StorePublishDirectory "$release/store" -ComplianceDirectory $notices `
  -ValidatedReceiverSha256 $testedReceiverSha256 -OutputPath $preflightReport
tests/packaging/Test-RuntimeNoticeCoverage.ps1 -PublishDirectory "$release/desktop" `
  -StorePublishDirectory "$release/store" -ComplianceDirectory $notices `
  -ValidatedReceiverSha256 $testedReceiverSha256 -OutputPath $coverageReport
```

Preflight mutates disposable payload subsets to verify version, receiver, scanner, SVG and controlled-library/notice rejection. Coverage tests copy notices only and use real layouts read-only to reject stale text, empty/count-mismatched coverage, wrong source/record hashes and fixture misuse. Neither generates packages or uses receiver ports.

The GitHub release test uses fake HTTP and credentials with a temporary workspace. It checks retrying GitHub's provisional `untagged-*` drafts, explicit final tag/commit selection, and refusal of ambiguous drafts or mismatched assets without making network requests. Published assets remain immutable; release records may be updated separately on the main branch.

Keep current toolchains, official sources/signed package cache, controlled prefixes/manifests, release artifacts/source bundle, previous upgrade baseline and final evidence. Unused superseded publish/bin/obj copies, old receiver build-key trees and failed staging/Sandbox inputs are cleanup candidates after necessary reports are retained. Reproducible caches are optional caches, not unused source code. Review an explicit size/path list and verify every deletion stays under `.runtime/`; preserve user pairing state and the only copy of release/source evidence.
