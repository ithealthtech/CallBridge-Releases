# Security architecture

## Trust boundaries

The desktop application, local service, PBX provider, and ConnectWise are separate trust boundaries. Data from every boundary is validated before use.

## Local service

The local .NET service remains bound to `127.0.0.1`. The launcher creates a random credential for each application session and passes it to the service and desktop through the process environment. `/health` is intentionally minimal and public; every other local route requires the bearer credential. Non-loopback host headers and foreign browser origins are rejected, request sizes and rates are bounded, and bearer comparison is constant-time.

The local service has no public webhook listener. Future provider event ingestion belongs in a controlled cloud broker with provider-specific signature verification and replay protection.

## Credentials

SIP passwords, ConnectWise PSA private keys, OAuth client secrets, and cached access tokens are excluded from JSON serialization and protected with Windows DPAPI. A future release should broker confidential OAuth client credentials through a controlled backend rather than distributing them to native clients.

## Sensitive data

Telephone numbers, contacts, call metadata, notes, recordings, ConnectWise identifiers, and provider event payloads are sensitive. Logs must use correlation identifiers and must not contain access tokens, passwords, complete telephone numbers, raw request headers, or raw third-party response bodies.

## Remaining production gates

- Design any future cloud event intake with provider signature validation and replay protection.
- Replace native confidential OAuth with browser-based PKCE or a backend integration broker.
- Add contact-data retention and deletion controls beyond the existing call-history retention, export, and confirmed deletion APIs.
- Add signed packaging and automated update verification.
- Complete telecom, E911, recording-consent, and CPNI review.
