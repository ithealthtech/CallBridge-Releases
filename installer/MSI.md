# CallBridge MSI Installer

The production installer path is WiX Toolset v4 because MSI is the standard package format for managed Windows deployments through Intune, Group Policy, RMM, SCCM, and similar tooling.

## Build

Install WiX Toolset v4:

```powershell
dotnet tool install --global wix
```

Then run:

```powershell
.\installer\build-msi.ps1 -Version 0.13.0
```

The output is written to:

```text
artifacts\installer\CallBridge-v0.13.0.msi
```

The MSI installs to `Program Files\CallBridge` and creates Start Menu and Desktop shortcuts that run `run-callbridge.cmd`, which starts the local service and desktop app together.

The build script refuses to package `settings.json`, `portal-secrets.json`, `.pfx`, `.pem`, or `.key` files.
