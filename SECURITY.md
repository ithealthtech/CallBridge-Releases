# Security policy

CallBridge runs a local HTTP service on a technician's workstation and handles telephony
data. Its central defensive assumption is that **the local machine is not a trusted
network** — anything running on it, including a web page in the technician's browser, is a
potential caller.

## Supported versions

Only the current stable CallBridge release receives security updates. Pre-release builds are
supported only for controlled testing.

## Reporting a vulnerability

Do not open a public GitHub issue for a suspected vulnerability. Report it privately to the
repository owner with:

- the affected version;
- reproduction steps;
- expected and observed behavior; and
- any known impact or suggested mitigation.

Never include real telephone numbers, call detail records, recordings, contact data,
ConnectWise identifiers, or production settings in a report. Use synthetic `555` numbers and
`example.invalid` domains.

> **Before publication:** a dedicated security contact must be added to this policy before
> the repository is made public.

## In scope

- Reaching any service route other than `/health` without the per-session bearer credential.
- Predicting, replaying, extracting, or reusing that credential across sessions.
- Driving the service from a browser origin or a non-loopback host header.
- Causing the service to bind to anything other than loopback.
- Bypassing TLS certificate validation for SIP signaling, or downgrading signaling.
- Extracting DPAPI-protected desktop secrets, or causing a secret to be written to
  serialized settings, logs, or a crash artifact.
- Reading, exporting, or deleting call history without authentication.
- Retention or deletion failing to actually remove data after the configured period or a
  confirmed delete.
- Leaking telephone numbers, call content, contact data, or ConnectWise identifiers into
  logs, telemetry, or error output.
- Privilege escalation, arbitrary file write, or code execution through the launcher, the
  service, or the ConnectWise integration.
- The launcher terminating a process that is not a verified orphaned CallBridge service.

## Not vulnerabilities

- **The service being reachable from the local machine itself.** It is a loopback service by
  design; authentication, origin checks, and host-header checks are the controls, not
  network isolation.
- **`/health` responding without a credential.** Intentional, and it exposes no sensitive
  data.
- **CallBridge not supporting emergency calling.** It is not approved for production
  emergency calling. That is a documented limitation, not a defect.
- **Unsigned local development builds.** Release artifacts are built by CI and code-signed
  through the approved release process; a locally built binary is not a release.

## Operator and developer expectations

- Never commit credentials, customer records, call detail records, databases, logs, packet
  captures, or production settings.
- Use synthetic `555` telephone numbers and `example.invalid` domains in tests and
  documentation.
- Treat telephone numbers, call history, recordings, contact data, ConnectWise identifiers,
  and support notes as sensitive.
- Rotate any credential immediately if it may have been exposed. Removing it from Git
  history is not sufficient.
- Release artifacts must be built by CI and code-signed through the approved release
  process.

## Design constraints that carry security weight

These are enforced properties, not preferences. See
[CONTRIBUTING.md](CONTRIBUTING.md) for the full list and
[docs/SECURITY-ARCHITECTURE.md](docs/SECURITY-ARCHITECTURE.md) for trust boundaries.

- The service binds only to loopback.
- All routes except `/health` require a random per-session bearer credential.
- Browser origins and non-loopback host headers are rejected.
- Request sizes and rates are bounded; logs omit sensitive payloads.
- TLS SIP signaling uses the Windows trust chain with **no certificate-bypass option**.
- Desktop secrets are DPAPI-protected and excluded from serialized settings.
- Call history has configurable retention, authenticated export, and confirmed deletion.
- Runtime databases and logs stay under the current user's local application-data
  directory.
