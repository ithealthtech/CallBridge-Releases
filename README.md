# CallBridge Softphone v0.9.0

Compiled Windows WPF softphone/control-center prototype for the white-label CallBridge VOIP app.

## Run

Double-click:

```text
run-softphone.cmd
```

The launcher starts the local CallBridge Internal v0.8 backend on `127.0.0.1:8787` when it is not already running, waits briefly for `/health`, then starts the desktop app.

If the `publish` folder is present, the launcher starts the compiled app. Otherwise it runs the project with the installed .NET 8 SDK.

## Included

- Axion-style white-label Windows app shell
- Modern MSP-grade visual treatment with softer geometry, richer color, gradients, icon navigation, and structured list rows
- Bundled IT HealthTech logo from the public `ithealthtech.com` site
- Dialpad and active-call controls
- Provider selector for mock, standard SIP test, and future Axion/Noixa integration
- Functional settings window
- Local settings persistence in `settings.json`
- Connector API health check against `http://127.0.0.1:8787`
- Call control wiring for the CallBridge Internal telephony endpoints
- Contacts, call history, messages, voicemail, parking, recordings, and MSP action views
- Double-click callable rows to load the dialer and start a call
- Settings validation and API connection testing
- Dashboard rows route to settings, health checks, or staged workflow notices
- Auto-start launcher for CallBridge Internal v0.8 so `127.0.0.1:8787` is not cold by default
- Contacts and call history use live backend endpoints only.
- Import real contacts from CSV using `Company,Contact,Phone` columns. Imports are persisted by CallBridge Internal.
- Add, edit, and delete individual contacts from the Contacts screen.
- Outbound call lifecycle events are persisted and displayed in Call history.
- Registration and call state now come from the connector API; failed operations never display as successful.
- SIP passwords are protected with the signed-in Windows account and stored under Local AppData.
- A single-instance guard prevents duplicate desktop processes.
- The launcher replaces orphaned port 8787 listeners and releases the port when the app closes.
- Demo caller/contact fallback data has been removed from phone views.

## Provider status

The mock provider is usable now for app workflow testing.

The standard SIP test provider is ready for credentials from a SIP service that allows direct SIP registration. Axion/Noixa remains intentionally blocked until Axion supplies approved SBC/WebRTC or portal API integration details.

## Build

```powershell
dotnet build .\CallBridge.Desktop.csproj -c Release
dotnet publish .\CallBridge.Desktop.csproj -c Release -o .\publish
```
