# CallBridge Softphone v0.10.0

White-label Windows VoIP control center for IT Health Technologies with Standard SIP calling and ConnectWise support workflows.

## Run

Double-click `run-softphone.cmd`. The launcher:

- closes an orphaned listener on `127.0.0.1:8787`;
- starts CallBridge Internal v0.12 in the background;
- waits for a successful health check;
- starts one desktop-app instance; and
- stops the backend and frees port 8787 when the app closes.

Requirements: Windows 10 or 11, Node.js 22.5 or newer, and the .NET 8 Desktop Runtime.

## ConnectWise setup

Open Settings. CallBridge supports the two distinct ConnectWise authentication models without mixing their credentials.

### ConnectWise Platform OAuth

Use credentials generated in ConnectWise Platform under **Integrations > API Access > Generate Access**:

- regional Platform API URL (NA, EU, or AU);
- OAuth Client ID;
- OAuth Client Secret; and
- scopes, initially `platform.companies.read platform.tickets.create`.

Choose **Test Platform OAuth**. The secret and access-token cache are protected with Windows DPAPI. Tokens are reused until expiry, including across app restarts, to avoid HTTP 423. Platform requests honor the documented 500-request, 5-minute quota and pause on HTTP 429 until the `Reset` time.

### ConnectWise PSA API member

Live PSA directory synchronization, company deep links, and service-ticket creation currently require:

- PSA Site URL;
- Company ID;
- API-member Public Key;
- API-member Private Key;
- ConnectWise Client ID header; and
- default Service Board ID.

Choose **Test PSA**, then open Contacts and select **Sync ConnectWise**. Select a synchronized contact to open its company or create a service ticket.

## Standard SIP setup

Choose **Standard SIP**, then enter a SIP registrar/domain, extension username, and password. Register before dialing. Calls use the Windows default communications microphone and speaker and support hangup, mute, hold/resume, DTMF, and blind transfer.

Axion/Noixa direct SIP remains unavailable because that service does not permit direct SIP dialing. Its provider entry stays blocked until approved SBC, WebRTC, or portal API details are supplied.

## Included workflows

- Live ConnectWise contact/company phone synchronization with no demo contacts
- Contact search, add, edit, delete, and CSV import
- Grouped call journal with duration, company/contact match, notes, outcome, and CSV export
- ConnectWise company deep links and service-ticket creation
- ConnectWise Platform OAuth validation, token caching, and rate-limit protection
- Encrypted SIP, PSA private-key, and Platform client-secret storage under Local AppData
- Connector health, operational dashboard, and live-only data views
- Single-instance desktop guard and reliable port cleanup

Messages, voicemail, call parking, and recordings remain provider-dependent and display no fabricated data when no provider is connected.

## Build and verify

```powershell
dotnet build .\CallBridge.Desktop.csproj -c Release
dotnet publish .\CallBridge.Desktop.csproj -c Release -o .\publish
dotnet run --project .\tests\CredentialSmokeTest.csproj -c Release
dotnet run --project .\tests\ConnectWiseSmoke\ConnectWiseSmoke.csproj -c Release
dotnet run --project .\tests\SipMediaSmoke\SipMediaSmoke.csproj -c Release
```
