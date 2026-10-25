# Contributing

Karmac is a Windows C# / WPF application on .NET Framework 4.8. Build and run
the core checks with `powershell -File build.ps1 -Test`; no NuGet restore is needed.
The Python artwork tools require Pillow, but Python is not an app dependency.

For a bug report, include the app version, Windows version, headphone model,
expected behavior and reproduction steps. Remove personal device names,
Bluetooth addresses, device/container IDs and local paths from any screenshots
or log snippets. Do not attach `settings.json`, a whole diagnostic dump, or your
local app-data folder. See [privacy](PRIVACY.md).

Report vulnerabilities using [SECURITY.md](SECURITY.md), not a public bug report.

For a change, keep the scope focused, describe the resulting behavior, and run:

```powershell
.\build.ps1 -Test
.\package.ps1
.\tools\Test-ReleaseVerification.ps1
```

UI checks (`tools/Verify-UI.ps1 -Background`) also require a locally paired
supported device. They use synthetic connection/power backends and do not prove
physical compatibility. Do not turn hardware-dependent checks into CI requirements.
Changes to real Bluetooth behavior need explicit opt-in hardware testing; never
connect or disconnect someone else's devices without their consent.

Keep secrets, user settings, real device captures, generated release ZIPs and
certificates out of commits. Preserve upstream license notices and artwork
provenance. Add regression coverage at the relevant behavior boundary when fixing
a bug. New model artwork or catalog recognition must not imply hardware testing.
