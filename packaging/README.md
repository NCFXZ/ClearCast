# Windows release packages

Build the native receiver as described in the project README, then publish both WinUI layouts from the repository root.

```powershell
dotnet publish ClearCast.csproj -c Release -r win-x64 -o .runtime/release/desktop -p:Version=1.0.0
dotnet publish ClearCast.csproj -c Release -r win-x64 -o .runtime/release/store -p:Version=1.0.0 `
  -p:BaseIntermediateOutputPath=.runtime/release/store-obj/ -p:BaseOutputPath=.runtime/release/store-bin/ `
  -p:ProjectPriFileName=resources.pri -p:ProjectPriIndexName=NCFX.ClearCast
```

Use WiX 5.0.2, its `WixToolset.Bal.wixext` extension, and the Windows SDK MakeAppx tool. Pass the binary distribution notices and `licenses` folder for the exact published dependencies as `-ComplianceDirectory`.

```powershell
./packaging/Build-Packages.ps1 -PublishDirectory .runtime/release/desktop `
  -StorePublishDirectory .runtime/release/store -ComplianceDirectory $notices `
  -OutputDirectory .runtime/release/packages -WixPath $wix -WixBalExtensionPath $bal -MakeAppxPath $makeappx
```

The script produces a self-contained EXE installer and an unsigned Store MSIX. Partner Center signs the MSIX during certification.
