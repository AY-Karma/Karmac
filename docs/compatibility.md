# Compatibility and limits

Karmac reconnects an existing Windows Bluetooth bond. Pair once in Windows; it does not bypass pairing consent, install drivers, change Bluetooth transmit power, or guarantee a takeover from another host.

Requires x64 Windows 10/11, .NET Framework 4.8, working Bluetooth/audio drivers, and permission to control the radio. A hardware switch, airplane-mode policy, or administrator restriction can prevent radio activation. An active call or another host can prevent the headphones accepting a handoff. Apps with their own output selection may keep it.

## Evidence by family

| Catalog family | Evidence in this release |
| --- | --- |
| AirPods Pro 3 | Physical reconnect/output checks and two observed Bluetooth-off cold connections on one laptop. Rotating BLE ownership remains unresolved in the tested portable setup. |
| AirPods, other AirPods Pro, AirPods Max | Catalog identity, UI, artwork, and synthetic state checks. No physical connection-speed guarantee. |
| Beats earbuds, headphones, and neckband/cabled wireless models | Catalog identity, UI, artwork, and synthetic state checks. No physical connection-speed guarantee. |

The catalog contains 30 identifiers. A catalog label, render, or battery layout establishes recognition only; it is not manufacturer certification, proof of product specifications, or a hardware-tested support list. Family illustrations are approximate and shared between generations. See [artwork provenance](../assets/NOTICE.txt).

## Battery sources

- Windows device-associated battery metadata or a standard Bluetooth battery service, when available. Windows metadata can be cached and is labelled accordingly. A single device percentage is not invented into separate left/right/case values.
- Supported Apple BLE payloads only when their identity can be tied to the selected paired device. Decoder fixtures cover IDs `2002`, `200F`, `2013`, `2019`, `201B`, `200E`, `2014`, `2024`, `2027`, `200A`, and `201F`; this is format coverage, not a physical test of each model.
- An optional nearby source explicitly selected by the user. It is labelled **manual/unverified**, is session-only, expires after 30 minutes at most, and does not authorize connection to that broadcaster. Battery readings become stale after 90 seconds without an update.

AirPods Pro 3 broadcasts were observed, but the tested app could not verify their rotating identity as the saved pair. Separate battery values can therefore remain unavailable. Signal strength or matching model alone never proves ownership. The nearby filter is a signal threshold, not a fixed pairing radius. Charge percentage is not battery health, capacity, or wear.

Karmac does not provide iCloud pairing, Find My, Apple Automatic Switching, firmware updates, ANC controls, a new Bluetooth codec, or a lower audio transport latency. See [measured observations](metrics.md); there is no completed Windows Settings baseline or measured speedup claim.
