# Security policy

## Supported versions

Only the current stable CallBridge release receives security updates. Pre-release builds are supported only for controlled testing.

## Reporting a vulnerability

Do not open a public GitHub issue for a suspected vulnerability. Report it privately to the repository owner with:

- the affected version;
- reproduction steps;
- expected and observed behavior; and
- any known impact or suggested mitigation.

A dedicated security contact must be added before the repository is published.

## Security expectations

- Never commit credentials, customer records, call detail records, databases, logs, packet captures, or production settings.
- Use synthetic `555` telephone numbers and `example.invalid` domains in tests and documentation.
- Treat telephone numbers, call history, recordings, contact data, ConnectWise identifiers, and support notes as sensitive.
- Rotate any credential immediately if it may have been exposed.
- Release artifacts must be built by CI and code-signed through the approved release process.
