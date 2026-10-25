# Public preview readiness review

Reviewed on 25 October 2026 for `1.0.0-preview.2`.

## Verified locally

- Gitleaks 8.30.1 found no credentials in the history reachable from published
  `main` and `v1.0.0-preview.1` (two commits), or in the proposed working source.
  Its downloaded Windows archive was checked against its release SHA-256 manifest.
  This is a credential scan, not a guarantee that every possible secret is absent.
- The original icon includes nine sizes from 16 to 256px. Windows loads the native
  executable icon; its embedded tray icon matches `assets/Karmac.ico` exactly.
- Core tests passed: 53 tests, 978 assertions. Artwork checks passed: 71.
  Background WPF verification passed 259 checks, including idle animation regression
  tests. These checks do not claim physical compatibility with every catalog model.
- Packaging verifies the version, x64 executable, signing status, SHA-256 manifest,
  release notes and exact portable-file allowlist. Tampered EXEs, wrong versions
  and unexpected archive entries were rejected by dedicated tests.
- MIT and upstream BSD/MIT notices are preserved. The new icon is original project
  geometry. Product illustration provenance and trademark independence are disclosed.
- README images were visually reviewed: their devices and battery states are
  synthetic presentation fixtures, labelled as such, without personal device names.
  The PNGs contain no text or EXIF metadata. Generated diagnostic screenshots and
  settings stay outside the release allowlist and Git.
- No app-owned accounts, analytics or update service were introduced. Local device
  data and logs are described in `PRIVACY.md`. Normal launch requests no elevation.
- GitHub workflows use pinned action revisions and read-only default permissions;
  only the explicit draft-release job receives repository write permission.

## Before opening the repository and publishing

The repository was still **private** at review time. This review did not change
visibility, publish a release, move a tag or enable a reporting channel.

1. Commit the reviewed source, rerun validation from that clean commit, and choose
   the repository visibility deliberately. Public visibility exposes its published
   Git history and existing releases, not only the latest working files.
2. Once public, enable GitHub **private vulnerability reporting** and verify the
   Report a vulnerability button before advertising it as available. GitHub only
   supports this feature for public repositories; the private-repo API was unavailable
   at review time. [GitHub instructions](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository).
3. Consider branch protection with the Windows build check and a protected `release`
   environment. No release environment was configured at review time; branch
   protection on this private repository returned GitHub's plan/visibility restriction.
4. Create the new `v1.0.0-preview.2` tag and use the draft-release workflow. Do not
   overwrite the older preview tag. Inspect its downloaded archive, then publish
   the draft and verify the Releases page as a signed-out visitor.
5. Run a clean-machine launch/install-extract test on supported Windows versions
   before promoting beyond preview. Local UI checks do not replace this test.

The package is **unsigned**. That is explicitly disclosed; signing credentials
have not been supplied and no trusted publisher identity is claimed. See
[Windows trust](windows-trust.md) and [release preparation](releasing.md).

Re-run credential scanning whenever the publication scope changes. For all local
branches and tags, use `gitleaks git . --redact --log-opts="--branches --tags"`.
Scan any additional refs before making them public; local app checkpoint refs were
not included in the two published commits reviewed above. Keep reports under the
ignored `artifacts/` directory, never paste unredacted credentials into issues.
