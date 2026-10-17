# Preparing a release

The repository and preview releases are private under `AY-Karma/Karmac`. Downloads require repository access. The current preview is `1.0.0-preview.1`; the executable carries this informational version and file version `1.0.0.0`.

## Local validation

```powershell
.\package.ps1
.\tools\Verify-Release.ps1
.\tools\Verify-UI.ps1 -Background -CaptureDirectory artifacts\ui-release
```

Core and artwork checks run during packaging. UI checks require the development machine's paired audio device and run separately; all connection/power mutations in that harness use synthetic backends. They do not belong in hardware-free CI as proof of physical compatibility. The ordinary GitHub build job compiles, tests, verifies, and uploads an Actions artifact; an Actions artifact is not a public release download.

Local outputs under `artifacts/releases/v1.0.0-preview.1/` are the portable ZIP, SHA-256 manifest, release notes, and machine-readable metadata. The EXE checksum refers to the executable inside the ZIP. Packaging uses a shared explicit allowlist and checks every entry against current source/build files. Release metadata records signing status and the commit when Git is available. Local research, diagnostic logs, keys, settings, and custom artwork are excluded.

## Before the first public release

1. Review the existing private repository's settings before changing visibility. Enable **private vulnerability reporting** when the repository becomes public, and update `SECURITY.md` only after verifying that channel. Configure the `release` environment and any desired maintainer approval protection.
2. Review the source files to be committed, including screenshots. Keep `artifacts/`, generated ZIPs, secrets, and personal diagnostics out of Git. The provided ignore patterns are a guard, not a credential audit.
3. Confirm the README's `AY-Karma/Karmac` release ZIP and checksum URLs match the published assets. Update the private-preview sentence before any public release, retaining the unsigned-build disclosure. For this prerelease, keep explicit `releases/download/v1.0.0-preview.1/` links; `releases/latest` may omit prereleases. Regenerate the package after documentation changes.
4. Build from the exact clean commit intended for release. Create and push its matching `v1.0.0-preview.1` tag only when publication is authorized. Run **Prepare draft release** with that existing tag. It builds the tagged source, checks version and files, and creates a **draft**, not an automatically public release.
5. Inspect the draft's downloaded ZIP, checksums, notes, signature status, and compatibility disclosures. Publish only when those match. The workflow does not enable repository reporting/settings or claim a trusted signature for you.

The current build is unsigned; [signing preparation](windows-trust.md) documents the trusted signing path. Do not commit certificates or encourage disabling Windows protection. After signing a tested binary, `package.ps1 -UseExistingBuild` packages those exact bytes and recalculates checksums without recompiling. Normal CI always builds/tests from source.

For a later version, update the assembly informational/file versions, add matching `docs/releases/<version>.md`, update the portable allowlist, and pass `-Version <version>` to packaging. Previously measured connection times must remain labelled with their actual hardware, sample count, and original date; they are not automatically measurements of a new build.

README screenshots are synthetic presentation fixtures, not measurements. To regenerate them after UI changes, run the background UI checks above, then `python tools/Create-ReadmeVisuals.py` with Pillow installed on the development machine. This is a documentation tool, not an app dependency. See GitHub's [release asset link documentation](https://docs.github.com/en/repositories/releasing-projects-on-github/linking-to-releases).
