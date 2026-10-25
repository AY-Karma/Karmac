# Security policy

Karmac releases are experimental previews. Only the latest published preview receives fixes; there is no response-time or support guarantee. Use the latest release rather than an older preview.

## Report a vulnerability

When GitHub private vulnerability reporting is enabled, use **Security → Advisories → Report a vulnerability** in this repository. If that button is unavailable, contact the maintainer through an existing private channel, or open an issue asking for a private reporting channel **without exploit details**. Do not post credentials, Bluetooth addresses, personal device dumps, or an unpatched vulnerability publicly.

Maintainers must enable and verify private vulnerability reporting when making the repository public. A reporting button is not promised until it is enabled. See [release preparation](docs/releasing.md).

Include the affected release, Windows version, reproduction steps, expected/observed behavior, and a minimal redacted example. Ordinary connection failures can be reported as bugs after redacting device names and IDs. See [privacy](PRIVACY.md).

## Trust boundaries

Connection targets must be revalidated against the Windows bond; a nearby advertisement, friendly name, or RSSI alone cannot authorize a connection. Manual battery sources remain explicitly unverified. Local images are size-limited and decoded through Windows imaging, but image parsing and device APIs still depend on Windows security updates. Normal operation does not require administrator privileges.

Current builds are unsigned. Verify the release checksum and review [Windows trust guidance](docs/windows-trust.md). A checksum detects changed bytes against a trusted manifest; it does not establish a publisher identity or replace code signing.
