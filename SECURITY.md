# Security policy

The initial `1.0.0-preview.1` build is an experimental preview. Only the latest published preview will receive fixes; there is no response-time or support guarantee. This repository and its releases are private.

## Report a vulnerability

While the repository is private, contact the maintainer through an existing private channel, or ask for a reporting channel without including exploit details. Do not post credentials, Bluetooth addresses, personal device dumps, or an unpatched vulnerability publicly. GitHub private vulnerability reporting must be enabled and verified if this repository later becomes public; that channel is not currently advertised as live.

Include the affected release, Windows version, reproduction steps, expected/observed behavior, and a minimal redacted example. Ordinary connection failures can be reported as bugs after redacting device names and IDs. See [privacy](PRIVACY.md).

## Trust boundaries

Connection targets must be revalidated against the Windows bond; a nearby advertisement, friendly name, or RSSI alone cannot authorize a connection. Manual battery sources remain explicitly unverified. Local images are size-limited and decoded through Windows imaging, but image parsing and device APIs still depend on Windows security updates. Normal operation does not require administrator privileges.

Current builds are unsigned. Verify the release checksum and review [Windows trust guidance](docs/windows-trust.md). A checksum detects changed bytes against a trusted manifest; it does not establish a publisher identity or replace code signing.
