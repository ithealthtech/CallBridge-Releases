# Security architecture

## Trust boundaries

The desktop application, local service, PBX provider, and ConnectWise are separate trust boundaries. Data from every boundary is validated before use.

## Local service

The local .NET service is a desktop-owned sidecar rather than a machine-wide Windows service. This keeps per-user call history and integration data in the user's security context, avoids administrator rights during normal use, and ensures the service runs only while CallBridge is open. The packaged desktop creates a random credential for each session, passes it to the child through the process environment, drains both child output streams, checks health every five seconds, and restarts the child after an unexpected exit or three consecutive health failures. Shutdown terminates only a child process owned by that desktop session. An explicitly configured external service is monitored but never terminated or restarted by the desktop.

The service remains bound to `127.0.0.1`. `/health` is intentionally minimal and public; every other local route requires the bearer credential. Non-loopback host headers and foreign browser origins are rejected, request sizes and rates are bounded, and bearer comparison is constant-time.

The desktop is single-instance per interactive Windows session. A second launch uses a session-local activation signal and foreground-window request, then exits before constructing SIP, media, or service components. The activation signal carries no credentials or customer data and only asks the existing window to restore and activate.

An inbound call opens a topmost screen-pop window and requests the foreground so the caller is visible over other applications. The pop shows only data CallBridge already holds or fetches with the configured credentials: the local directory match from the authenticated `/matches` route and up to five open ConnectWise tickets for a numeric company ID. The company ID is validated before it is placed in a ConnectWise query, lookups time out after five seconds and are cancelled when ringing stops, and lookup failures are logged by exception type only, without telephone numbers or response bodies. Call notes are sent to ConnectWise only when the technician saves them or ends a call with a ticket selected; they are posted as internal-analysis notes to a validated numeric ticket ID and are never written to logs.

Optional launch-at-sign-in uses the current user's Windows Run registry key. CallBridge validates that the target is an existing `CallBridge.Desktop.exe`, emits a quoted command, and repairs an enabled stale path on a later successful launch. Disabling the option removes only the CallBridge-owned registry value.

The local service has no public webhook listener. Future provider event ingestion belongs in a controlled cloud broker with provider-specific signature verification and replay protection.

Settings are split into technician sections (Audio and Sign-in) and admin sections (phone account, SIP, ConnectWise, branding). When an admin PIN is set, admin sections require it; the PIN is stored only as a salted PBKDF2-SHA256 hash (210,000 iterations), compared in constant time, slowed after repeated failures, and relocked whenever the user leaves Settings. The PIN is a workflow control that keeps technicians out of provider settings in the app. It is not a security boundary against someone who can edit the current Windows user's settings file; enforced configuration should come from an installer or managed policy.

Brand logos are treated as untrusted files: only PNG and JPEG files of 1 MB or less are accepted, they must decode as images no larger than 2048 × 2048 pixels, and they are copied into the CallBridge branding folder under the user's local application data before use. Removal deletes only files inside that folder.

## Credentials

SIP passwords, ConnectWise PSA private keys, OAuth client secrets, and cached access tokens are excluded from JSON serialization and protected with Windows DPAPI. Settings are replaced atomically. Unreadable configuration is encrypted before quarantine and defaults are created only after the original file has been preserved. Pending call lifecycle records are also stored in a bounded, DPAPI-protected queue so local-service interruptions do not lose history. A future release should broker confidential OAuth client credentials through a controlled backend rather than distributing them to native clients.

## Sensitive data

Telephone numbers, contacts, call metadata, notes, recordings, ConnectWise identifiers, and provider event payloads are sensitive. Logs must use correlation identifiers and must not contain access tokens, passwords, complete telephone numbers, raw request headers, or raw third-party response bodies.

## Remaining production gates

- Design any future cloud event intake with provider signature validation and replay protection.
- Replace native confidential OAuth with browser-based PKCE or a backend integration broker.
- Add contact-data retention and deletion controls beyond the existing call-history retention, export, and confirmed deletion APIs.
- Add signed packaging and automated update verification.
- Retain provider-owned emergency-dialing compatibility as a deferred backlog item; CallBridge does not implement carrier routing or location infrastructure.
- Complete recording-consent and applicable customer-data privacy review before enabling any future recording feature.
