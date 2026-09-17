# CallBridge MSI Installer

The production installer path is WiX Toolset v4 because MSI is the standard package format for managed Windows deployments through Intune, Group Policy, RMM, SCCM, and similar tooling.

## Build

Install WiX Toolset v4:

```powershell
dotnet tool install --global wix
```

Then run:

```powershell
.\installer\build-msi.ps1 -Version 0.14.0
```

The output is written to:

```text
artifacts\installer\CallBridge-v0.14.0.msi
```

The MSI installs a self-contained .NET 10 Windows x64 payload to `Program Files\CallBridge` and creates Start Menu and Desktop shortcuts for CallBridge. No separate .NET runtime installation is required.

The build script refuses to package `settings.json`, `portal-secrets.json`, `.pfx`, `.pem`, or `.key` files.
