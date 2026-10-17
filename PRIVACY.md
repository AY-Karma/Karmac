# Privacy

Karmac runs locally. The current application has no account system, analytics SDK, advertising, crash-upload service, or application-owned HTTP API. It uses Windows Bluetooth, device metadata, and audio APIs. Windows and drivers can perform their own services under Windows settings; this document does not describe those services as offline.

## Local data

| Location under `%LOCALAPPDATA%\Karmac` | Contents |
| --- | --- |
| `settings.json` (and a temporary file during saves) | Preferences, selected device name, Windows container/device IDs, Bluetooth address, and model hint. The hint is revalidated before connecting. |
| `app.log` | Local diagnostic exceptions and timestamps. Errors can contain device identifiers or local file paths. The file is cleared before a subsequent write once it exceeds 256 KiB; this is not a strict per-entry size cap. |
| `Artwork\` | Optional images supplied by the user. These are never added to release archives. |

For existing installations, settings and artwork can be read from `%LOCALAPPDATA%\OpenConnect` when the corresponding Karmac data is absent. New settings/log writes use Karmac. The old folder is not deleted automatically.

Bluetooth discovery and the battery listener process nearby addresses, model identifiers, signal strength, and available charge/charging/in-ear flags in memory. Identity/readings are pruned on incoming events after approximately three minutes; stale readings are not displayed. An explicitly chosen nearby source expires after at most 30 minutes and is not saved to disk. Closing to the tray keeps the process running; **Quit app** stops it.

Development diagnostics such as `--diagnose <path>` explicitly write device details to the requested file. They are not part of normal launch or uploaded automatically. Review and redact logs, diagnostic output, and screenshots before sharing: device names can include a person's name, and IDs/addresses can identify hardware. Never share `settings.json` or an entire local-data folder by default.

To remove local data, quit Karmac, delete its portable files and `%LOCALAPPDATA%\Karmac`, and remove the old OpenConnect folder if present and no longer wanted. This does not remove the Bluetooth bond stored by Windows; unpair through Windows Settings if needed.
